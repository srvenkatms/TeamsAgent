﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Agents.Authentication;
using Microsoft.Agents.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.IdentityModel.Validators;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Microsoft.Agents.AspNetAuthentication;

public static class AspNetExtensions
{
    private static readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _openIdMetadataCache = new();

    /// <summary>
    /// Adds AspNet token validation typical for ABS/SMBA and agent-to-agent using settings in configuration.
    /// </summary>
    /// <param name="services"></param>
    /// <param name="configuration"></param>
    /// <param name="tokenValidationSectionName">Name of the config section to read.</param>
    /// <param name="logger">Optional logger to use for authentication event logging.</param>
    /// <remarks>
    /// <para>This extension reads <see cref="TokenValidationOptions"/> settings from configuration.  If configuration is missing JWT token
    /// is not enabled.</para>
    /// The minimum, but typical, configuration is:
    /// <code>
    /// "TokenValidation": {
    ///    "Audiences": [
    ///      "{{ClientId}}" // this is the Client ID used for the Azure Bot
    ///    ],
    ///    "TenantId": "{{TenantId}}"
    /// }
    /// </code>
    /// </remarks>
    public static void AddAgentAspNetAuthentication(this IServiceCollection services, IConfiguration configuration, string tokenValidationSectionName = "TokenValidation")
    {
        IConfigurationSection tokenValidationSection = configuration.GetSection(tokenValidationSectionName);

        if (!tokenValidationSection.Exists() || !tokenValidationSection.GetValue("Enabled", true))
        {
            // Noop if TokenValidation section missing or disabled.
            System.Diagnostics.Trace.WriteLine("AddAgentAspNetAuthentication: Auth disabled");
            return;
        }

        services.AddAgentAspNetAuthentication(tokenValidationSection.Get<TokenValidationOptions>()!);
    }

    /// <summary>
    /// Adds AspNet token validation typical for ABS/SMBA and agent-to-agent.
    /// </summary>
    public static void AddAgentAspNetAuthentication(this IServiceCollection services, TokenValidationOptions validationOptions)
    {
        AssertionHelpers.ThrowIfNull(validationOptions, nameof(validationOptions));

        // Must have at least one Audience.
        if (validationOptions.Audiences == null || validationOptions.Audiences.Count == 0)
        {
            throw new ArgumentException($"{nameof(TokenValidationOptions)}:Audiences requires at least one ClientId");
        }

        // Audience values must be GUID's
        foreach (var audience in validationOptions.Audiences)
        {
            if (!Guid.TryParse(audience, out _))
            {
                throw new ArgumentException($"{nameof(TokenValidationOptions)}:Audiences values must be a GUID");
            }
        }

        // If ValidIssuers is empty, default for ABS Public Cloud
        if (validationOptions.ValidIssuers == null || validationOptions.ValidIssuers.Count == 0)
        {
            validationOptions.ValidIssuers =
            [
                "https://api.botframework.com",
                "https://sts.windows.net/d6d49420-f39b-4df7-a1dc-d59a935871db/",
                "https://login.microsoftonline.com/d6d49420-f39b-4df7-a1dc-d59a935871db/v2.0",
                "https://sts.windows.net/f8cdef31-a31e-4b4a-93e4-5f571e91255a/",
                "https://login.microsoftonline.com/f8cdef31-a31e-4b4a-93e4-5f571e91255a/v2.0",
                "https://sts.windows.net/69e9b82d-4842-4902-8d1e-abc5b98a55e8/",
                "https://login.microsoftonline.com/69e9b82d-4842-4902-8d1e-abc5b98a55e8/v2.0",
            ];

            if (!string.IsNullOrEmpty(validationOptions.TenantId) && Guid.TryParse(validationOptions.TenantId, out _))
            {
                validationOptions.ValidIssuers.Add(string.Format(CultureInfo.InvariantCulture, AuthenticationConstants.ValidTokenIssuerUrlTemplateV1, validationOptions.TenantId));
                validationOptions.ValidIssuers.Add(string.Format(CultureInfo.InvariantCulture, AuthenticationConstants.ValidTokenIssuerUrlTemplateV2, validationOptions.TenantId));
            }
        }

        // If the `AzureBotServiceOpenIdMetadataUrl` setting is not specified, use the default based on `IsGov`.  This is what is used to authenticate ABS tokens.
        if (string.IsNullOrEmpty(validationOptions.AzureBotServiceOpenIdMetadataUrl))
        {
            validationOptions.AzureBotServiceOpenIdMetadataUrl = validationOptions.IsGov ? AuthenticationConstants.GovAzureBotServiceOpenIdMetadataUrl : AuthenticationConstants.PublicAzureBotServiceOpenIdMetadataUrl;
        }

        // If the `OpenIdMetadataUrl` setting is not specified, use the default based on `IsGov`.  This is what is used to authenticate Entra ID tokens.
        if (string.IsNullOrEmpty(validationOptions.OpenIdMetadataUrl))
        {
            validationOptions.OpenIdMetadataUrl = validationOptions.IsGov ? AuthenticationConstants.GovOpenIdMetadataUrl : AuthenticationConstants.PublicOpenIdMetadataUrl;
        }

        var openIdMetadataRefresh = validationOptions.OpenIdMetadataRefresh ?? BaseConfigurationManager.DefaultAutomaticRefreshInterval;

        _ = services.AddAuthentication(options =>
        {
            options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
        })
        .AddJwtBearer(options =>
        {
            options.SaveToken = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromMinutes(5),
                ValidIssuers = validationOptions.ValidIssuers,
                ValidAudiences = validationOptions.Audiences,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
            };

            // Using Microsoft.IdentityModel.Validators
            options.TokenValidationParameters.EnableAadSigningKeyIssuerValidation();

            options.Events = new JwtBearerEvents
            {
                // Create a ConfigurationManager based on the requestor.  This is to handle ABS non-Entra tokens.
                OnMessageReceived = async context =>
                {
                    string authorizationHeader = context.Request.Headers.Authorization.ToString();

                    if (string.IsNullOrEmpty(authorizationHeader))
                    {
                        // Default to AadTokenValidation handling
                        context.Options.TokenValidationParameters.ConfigurationManager ??= options.ConfigurationManager as BaseConfigurationManager;
                        await Task.CompletedTask.ConfigureAwait(false);
                        return;
                    }

                    string[] parts = authorizationHeader?.Split(' ')!;
                    if (parts.Length != 2 || parts[0] != "Bearer")
                    {
                        // Default to AadTokenValidation handling
                        context.Options.TokenValidationParameters.ConfigurationManager ??= options.ConfigurationManager as BaseConfigurationManager;
                        await Task.CompletedTask.ConfigureAwait(false);
                        return;
                    }

                    JwtSecurityToken token = new(parts[1]);
                    string issuer = token.Claims.FirstOrDefault(claim => claim.Type == AuthenticationConstants.IssuerClaim)?.Value!;

                    if (validationOptions.AzureBotServiceTokenHandling && AuthenticationConstants.BotFrameworkTokenIssuer.Equals(issuer))
                    {
                        // Use the Azure Bot authority for this configuration manager
                        context.Options.TokenValidationParameters.ConfigurationManager = _openIdMetadataCache.GetOrAdd(validationOptions.AzureBotServiceOpenIdMetadataUrl, key =>
                        {
                            return new ConfigurationManager<OpenIdConnectConfiguration>(validationOptions.AzureBotServiceOpenIdMetadataUrl, new OpenIdConnectConfigurationRetriever(), new HttpClient())
                            {
                                AutomaticRefreshInterval = openIdMetadataRefresh
                            };
                        });
                    }
                    else
                    {
                        context.Options.TokenValidationParameters.ConfigurationManager = _openIdMetadataCache.GetOrAdd(validationOptions.OpenIdMetadataUrl, key =>
                        {
                            return new ConfigurationManager<OpenIdConnectConfiguration>(validationOptions.OpenIdMetadataUrl, new OpenIdConnectConfigurationRetriever(), new HttpClient())
                            {
                                AutomaticRefreshInterval = openIdMetadataRefresh
                            };
                        });
                    }

                    await Task.CompletedTask.ConfigureAwait(false);
                },

                OnTokenValidated = context =>
                {
                    return Task.CompletedTask;
                },
                OnForbidden = context =>
                {
                    return Task.CompletedTask;
                },
                OnAuthenticationFailed = context =>
                {
                    return Task.CompletedTask;
                }
            };
        });
    }

    public class TokenValidationOptions
    {
        public IList<string>? Audiences { get; set; }

        /// <summary>
        /// TenantId of the Azure Bot.  Optional but recommended. 
        /// </summary>
        public string? TenantId { get; set; }

        /// <summary>
        /// Additional valid issuers.  Optional, in which case the Public Azure Bot Service issuers are used.
        /// </summary>
        public IList<string>? ValidIssuers { get; set; }

        /// <summary>
        /// Can be omitted, in which case public Azure Bot Service and Azure Cloud metadata urls are used.
        /// </summary>
        public bool IsGov { get; set; } = false;

        /// <summary>
        /// Azure Bot Service OpenIdMetadataUrl.  Optional, in which case default value depends on IsGov.
        /// </summary>
        /// <see cref="AuthenticationConstants.PublicAzureBotServiceOpenIdMetadataUrl"/>
        /// <see cref="AuthenticationConstants.GovAzureBotServiceOpenIdMetadataUrl"/>
        public string? AzureBotServiceOpenIdMetadataUrl { get; set; }

        /// <summary>
        /// Entra OpenIdMetadataUrl.  Optional, in which case default value depends on IsGov.
        /// </summary>
        /// <see cref="AuthenticationConstants.PublicOpenIdMetadataUrl"/>
        /// <see cref="AuthenticationConstants.GovOpenIdMetadataUrl"/>
        public string? OpenIdMetadataUrl { get; set; }

        /// <summary>
        /// Determines if Azure Bot Service tokens are handled.  Defaults to true and should always be true until Azure Bot Service sends Entra ID token.
        /// </summary>
        public bool AzureBotServiceTokenHandling { get; set; } = true;

        /// <summary>
        /// OpenIdMetadata refresh interval.  Defaults to 12 hours.
        /// </summary>
        public TimeSpan? OpenIdMetadataRefresh { get; set; }
    }
}
