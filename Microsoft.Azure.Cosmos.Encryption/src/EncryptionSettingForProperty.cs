﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Encryption
{
    using System;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using global::Azure;
    using Microsoft.Data.Encryption.Cryptography;

    internal sealed class EncryptionSettingForProperty
    {
        private static readonly SemaphoreSlim EncryptionKeyCacheSemaphore = new SemaphoreSlim(1, 1);

        private readonly string databaseRid;

        private readonly EncryptionContainer encryptionContainer;

        public EncryptionSettingForProperty(
            string clientEncryptionKeyId,
            EncryptionType encryptionType,
            EncryptionContainer encryptionContainer,
            string databaseRid)
        {
            this.ClientEncryptionKeyId = string.IsNullOrEmpty(clientEncryptionKeyId) ? throw new ArgumentNullException(nameof(clientEncryptionKeyId)) : clientEncryptionKeyId;
            this.EncryptionType = encryptionType;
            this.encryptionContainer = encryptionContainer ?? throw new ArgumentNullException(nameof(encryptionContainer));
            this.databaseRid = string.IsNullOrEmpty(databaseRid) ? throw new ArgumentNullException(nameof(databaseRid)) : databaseRid;
        }

        public string ClientEncryptionKeyId { get; }

        public EncryptionType EncryptionType { get; }

        /// <summary>
        /// Builds AeadAes256CbcHmac256EncryptionAlgorithm object for the encryption setting.
        /// </summary>
        /// <param name="ifNoneMatchEtags">If-None-Match Etag associated with the request. </param>
        /// <param name="shouldForceRefresh"> force refresh encryption cosmosclient cache. </param>
        /// <param name="forceRefreshGatewayCache"> force refresh gateway cache. </param>
        /// <param name="cancellationToken"> cacellation token. </param>
        /// <returns>AeadAes256CbcHmac256EncryptionAlgorithm object to carry out encryption. </returns>
        public async Task<AeadAes256CbcHmac256EncryptionAlgorithm> BuildEncryptionAlgorithmForSettingAsync(
            string ifNoneMatchEtags,
            bool shouldForceRefresh,
            bool forceRefreshGatewayCache,
            CancellationToken cancellationToken)
        {
            ClientEncryptionKeyProperties clientEncryptionKeyProperties;
            try
            {
                clientEncryptionKeyProperties = await this.encryptionContainer.EncryptionCosmosClient.GetClientEncryptionKeyPropertiesAsync(
                    clientEncryptionKeyId: this.ClientEncryptionKeyId,
                    encryptionContainer: this.encryptionContainer,
                    databaseRid: this.databaseRid,
                    ifNoneMatchEtag: ifNoneMatchEtags,
                    shouldForceRefresh: shouldForceRefresh,
                    cancellationToken: cancellationToken);
            }
            catch (CosmosException ex)
            {
                // if there was a retry with ifNoneMatchEtags
                if (ex.StatusCode == HttpStatusCode.NotModified && !forceRefreshGatewayCache)
                {
                    throw new InvalidOperationException($"The Client Encryption Key needs to be rewrapped with a valid Key Encryption Key." +
                        $" The Key Encryption Key used to wrap the Client Encryption Key has been revoked: {ex.Message}." +
                        $" Please refer to https://aka.ms/CosmosClientEncryption for more details. ");
                }
                else
                {
                    throw;
                }
            }

            ProtectedDataEncryptionKey protectedDataEncryptionKey;

            try
            {
                // we pull out the Encrypted Data Encryption Key and build the Protected Data Encryption key
                // Here a request is sent out to unwrap using the Master Key configured via the Key Encryption Key.
                protectedDataEncryptionKey = await this.BuildProtectedDataEncryptionKeyAsync(
                    clientEncryptionKeyProperties,
                    this.encryptionContainer.EncryptionCosmosClient.EncryptionKeyStoreProvider,
                    this.ClientEncryptionKeyId,
                    cancellationToken);
            }
            catch (RequestFailedException ex)
            {
                // The access to master key was probably revoked. Try to fetch the latest ClientEncryptionKeyProperties from the backend.
                // This will succeed provided the user has rewraped the Client Encryption Key with right set of meta data.
                // This is based on the AKV provider implementaion so we expect a RequestFailedException in case other providers are used in unwrap implementation.
                // We dont retry if the etag was already tried upon.
                if (ex.Status == (int)HttpStatusCode.Forbidden && !string.Equals(clientEncryptionKeyProperties.ETag, ifNoneMatchEtags))
                {
                    if (!forceRefreshGatewayCache)
                    {
                        return await this.BuildEncryptionAlgorithmForSettingAsync(
                           ifNoneMatchEtags: null,
                           shouldForceRefresh: true,
                           forceRefreshGatewayCache: true,
                           cancellationToken: cancellationToken);
                    }
                    else
                    {
                        return await this.BuildEncryptionAlgorithmForSettingAsync(
                            ifNoneMatchEtags: clientEncryptionKeyProperties.ETag,
                            shouldForceRefresh: true,
                            forceRefreshGatewayCache: false,
                            cancellationToken: cancellationToken);
                    }
                }
                else
                {
                    throw;
                }
            }

            AeadAes256CbcHmac256EncryptionAlgorithm aeadAes256CbcHmac256EncryptionAlgorithm = new AeadAes256CbcHmac256EncryptionAlgorithm(
                   protectedDataEncryptionKey,
                   this.EncryptionType);

            return aeadAes256CbcHmac256EncryptionAlgorithm;
        }

        private async Task<ProtectedDataEncryptionKey> BuildProtectedDataEncryptionKeyAsync(
            ClientEncryptionKeyProperties clientEncryptionKeyProperties,
            EncryptionKeyStoreProvider encryptionKeyStoreProvider,
            string keyId,
            CancellationToken cancellationToken)
        {
            if (await EncryptionKeyCacheSemaphore.WaitAsync(-1, cancellationToken))
            {
                try
                {
                    KeyEncryptionKey keyEncryptionKey = KeyEncryptionKey.GetOrCreate(
                        clientEncryptionKeyProperties.EncryptionKeyWrapMetadata.Name,
                        clientEncryptionKeyProperties.EncryptionKeyWrapMetadata.Value,
                        encryptionKeyStoreProvider);

                    ProtectedDataEncryptionKey protectedDataEncryptionKey = ProtectedDataEncryptionKey.GetOrCreate(
                        keyId,
                        keyEncryptionKey,
                        clientEncryptionKeyProperties.WrappedDataEncryptionKey);

                    return protectedDataEncryptionKey;
                }
                finally
                {
                    EncryptionKeyCacheSemaphore.Release(1);
                }
            }

            throw new InvalidOperationException("Failed to build ProtectedDataEncryptionKey. ");
        }
    }
}