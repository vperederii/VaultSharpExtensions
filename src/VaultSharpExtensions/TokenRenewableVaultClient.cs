﻿using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using VaultSharp;
using VaultSharp.V1;
using VaultSharp.V1.AuthMethods.Token.Models;
using VaultSharp.V1.Commons;

namespace VaultSharpExtensions
{
    /// <summary>
    /// Implementation of IVaultClient which has periodic token refresh(reset) capabilities.
    /// This implementation is useful when Vault token has expiration.
    /// Implements IDisposable.
    /// </summary>
    /// <remarks>
    /// Note that usage of this class won't give any effect when using Token authentication method type.
    /// This is because static token is provided at the configuration stage. 
    /// So after resetting this token, the same one is retrieved from configuration without calling Vault 'login/' endpoint.
    /// </remarks>
    internal sealed class TokenRenewableVaultClient : IVaultClient, IDisposable
    {
        private static readonly double RenewTokenFactor = 0.9d;
        
        private Timer _tokenRefreshTimer;

        private readonly IVaultClient _impl;
        private readonly ILogger<TokenRenewableVaultClient> _logger;
        private bool _disposed = false;
        private readonly object _syncRoot = new object();
        private readonly TimeSpan _defaultDueTime;

        /// <summary>
        /// Constructs TokenRenewableVaultClient.
        /// </summary>
        /// <param name="vaultClient">
        /// Provides an interface to interact with Vault as a client. 
        /// This is the only entry point for consuming the Vault Client.
        /// </param>
        /// <param name="logger">Represents a type used to perform logging.</param>
        public TokenRenewableVaultClient(IVaultClient impl, ILogger<TokenRenewableVaultClient> logger) :
            this(impl, logger, TimeSpan.FromMinutes(1))
        {
        }

        /// <summary>
        /// Constructs TokenRenewableVaultClient.
        /// </summary>
        /// <param name="vaultClient">
        /// Provides an interface to interact with Vault as a client. 
        /// This is the only entry point for consuming the Vault Client.
        /// </param>
        /// <param name="logger">Represents a type used to perform logging.</param>
        /// <param name="defaultDueTime">Default due time of the timer for token periodic refresh.</param>
        internal TokenRenewableVaultClient(IVaultClient impl, 
                                            ILogger<TokenRenewableVaultClient> logger, 
                                            TimeSpan defaultDueTime)
        {
            _impl = impl;
            _logger = logger;
            _defaultDueTime = defaultDueTime;
            RenewAuthToken(true);
        }

        /// <inheritdoc/>
        public VaultClientSettings Settings => _impl.Settings;

        /// <inheritdoc/>
        public IVaultClientV1 V1 => _impl.V1;

        /// <inheritdoc/>
        public void Dispose()
        {
            lock (_syncRoot)
            {
                if (_disposed)
                {
                    return;
                }

                if (null != _tokenRefreshTimer)
                {
                    _tokenRefreshTimer.Dispose();
                    _tokenRefreshTimer = null;
                }

                _disposed = true;
            }
        }

        private void RenewAuthToken(bool initialCall, string tokenId = null)
        {
            TimeSpan dueTime = _defaultDueTime;
            bool setTimer = true;
            try
            {
                if (!initialCall)
                {
                    //After token reset a new one will be retrieved on a subsequent Vault endpoint call.
                    V1.Auth.ResetVaultToken();
                    _logger.LogInformation("Token with the ID=[{TokenId}] has been reset.", tokenId);
                }

                Secret<CallingTokenInfo> tokenInfo = V1.Auth.Token.LookupSelfAsync().GetAwaiter().GetResult();
                tokenId = tokenInfo.Data.Id;

                // Check if token had initial TTL. If it is equal to zero, then it never expires.
                // So there is no need to renew it later.
                if (0 == tokenInfo.Data.CreationTimeToLive)
                {
                    _logger.LogInformation("Token with the ID=[{TokenId}] has no expiration. So it won't be renewed", tokenId);
                    setTimer = false;
                    return;
                }

                int timeToRefresh = (int)(tokenInfo.Data.TimeToLive * RenewTokenFactor);
                dueTime = TimeSpan.FromSeconds(timeToRefresh);

                _logger.LogInformation("Token with the ID=[{TokenId}] will be renewed in {MinutesToRenew:N2} minutes.", tokenInfo.Data.Id, dueTime.TotalMinutes);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh vault token.");
            }
            finally
            {
                lock (_syncRoot)
                {
                    if (!_disposed)
                    {
                        if (null != _tokenRefreshTimer)
                        {
                            _tokenRefreshTimer.Dispose();
                        }

                        _tokenRefreshTimer = setTimer ? new Timer(TimerCallback, tokenId, dueTime, TimeSpan.FromMilliseconds(-1)) : null;
                    }
                }
            }
        }

        private void TimerCallback(object state)
        {
            RenewAuthToken(false, (string)state);
        }
    }
}
