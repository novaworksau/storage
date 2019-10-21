﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Storage;
using Microsoft.Azure.Storage.Auth;
using Microsoft.Azure.Storage.Blob;
using NetBox.Extensions;
using Storage.Net.Blobs;
using AzureStorageException = Microsoft.Azure.Storage.StorageException;

namespace Storage.Net.Microsoft.Azure.Storage.Blobs
{
   class AzureUniversalBlobStorageProvider : IAzureBlobStorage
   {
      private const int BrowserParallelism = 10;

      private readonly ConcurrentDictionary<string, CloudBlobContainer> _containerNameToContainer =
         new ConcurrentDictionary<string, CloudBlobContainer>();

      private readonly CloudStorageAccount _account;
      private string _containerName;   // when limited to a single container

      private readonly CloudBlobClient _client;

      public AzureUniversalBlobStorageProvider(CloudBlobClient cloudBlobClient, CloudStorageAccount account)
      {
         _client = cloudBlobClient ?? throw new ArgumentNullException(nameof(cloudBlobClient));
         _account = account;
      }

      public static AzureUniversalBlobStorageProvider CreateFromAccountNameAndKey(string accountName, string key)
      {
         if(accountName == null)
            throw new ArgumentNullException(nameof(accountName));

         if(key == null)
            throw new ArgumentNullException(nameof(key));

         var account = new CloudStorageAccount(
            new StorageCredentials(accountName, key),
            true);

         return new AzureUniversalBlobStorageProvider(account.CreateCloudBlobClient(), account);
      }

      public static AzureUniversalBlobStorageProvider CreateFromSasUrl(string sasUrl)
      {
         if(sasUrl is null)
            throw new ArgumentNullException(nameof(sasUrl));

         if(!TryParseSasUrl(sasUrl, out string accountName, out string containerName, out string sas))
            throw new ArgumentException("invalid url", nameof(sasUrl));

         var account = new CloudStorageAccount(new StorageCredentials(sas), accountName, null, true);

         return new AzureUniversalBlobStorageProvider(account.CreateCloudBlobClient(), null) { _containerName = containerName };
      }

      private static bool TryParseSasUrl(string url, out string accountName, out string containerName, out string sas)
      {
         try
         {
            var u = new Uri(url);

            accountName = u.Host.Substring(0, u.Host.IndexOf('.'));
            containerName = u.Segments.Length == 2 ? u.Segments[1] : null;
            sas = u.Query;

            return true;
         }
         catch
         {
            accountName = null;
            containerName = null;
            sas = null;
            return false;
         }

      }

      public static AzureUniversalBlobStorageProvider CreateForLocalEmulator()
      {
         if(CloudStorageAccount.TryParse(Constants.UseDevelopmentStorageConnectionString, out CloudStorageAccount account))
         {
            return new AzureUniversalBlobStorageProvider(account.CreateCloudBlobClient(), account);
         }
         else
         {
            throw new InvalidOperationException($"Cannot connect to local development environment when creating blob storage provider.");
         }

      }

      public async Task DeleteAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPaths(fullPaths);

         await Task.WhenAll(fullPaths.Select(fullPath => DeleteAsync(fullPath, cancellationToken)));
      }

      private async Task DeleteAsync(string fullPath, CancellationToken cancellationToken)
      {
         (CloudBlobContainer container, string path) = await GetPartsAsync(fullPath, false);

         CloudBlockBlob blob = string.IsNullOrEmpty(path)
            ? null
            : container.GetBlockBlobReference(StoragePath.Normalize(path, false));
         if(blob != null && await blob.ExistsAsync().ConfigureAwait(false))
         {
            await blob.DeleteIfExistsAsync();
         }
         else
         {
            //try deleting as a folder
            CloudBlobDirectory dir = container.GetDirectoryReference(StoragePath.Normalize(path, false));
            using(var browser = new AzureBlobDirectoryBrowser(container, _containerName == null, 3))
            {
               await browser.RecursiveDeleteAsync(dir, cancellationToken).ConfigureAwait(false);
            }
         }
      }

      public void Dispose()
      {

      }

      public async Task<IReadOnlyCollection<bool>> ExistsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
      {
         var result = new List<bool>();

         foreach(string fullPath in fullPaths)
         {
            GenericValidation.CheckBlobFullPath(fullPath);

            (CloudBlobContainer container, string path) = await GetPartsAsync(fullPath, false).ConfigureAwait(false);

            if(container == null)
            {
               result.Add(false);
            }
            else
            {
               CloudBlockBlob blob = container.GetBlockBlobReference(StoragePath.Normalize(path, false));
               bool exists = await blob.ExistsAsync().ConfigureAwait(false);
               result.Add(exists);
            }
         }

         return result;
      }

      public async Task<IReadOnlyCollection<Blob>> GetBlobsAsync(IEnumerable<string> fullPaths, CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPaths(fullPaths);
         return await Task.WhenAll(fullPaths.Select(id => GetBlobAsync(id, cancellationToken))).ConfigureAwait(false);
      }

      public async Task SetBlobsAsync(IEnumerable<Blob> blobs, CancellationToken cancellationToken = default)
      {
         if(blobs == null)
            return;

         foreach(Blob blob in blobs.Where(b => b != null))
         {
            (CloudBlobContainer container, string path) = await GetPartsAsync(blob, false).ConfigureAwait(false);
            CloudBlockBlob cloudBlob = container.GetBlockBlobReference(StoragePath.Normalize(path, false));
            await AttachBlobMetaAsync(cloudBlob, blob).ConfigureAwait(false);
         }
      }

      private async Task<Blob> GetBlobAsync(string fullPath, CancellationToken cancellationToken)
      {
         (CloudBlobContainer container, string path) = await GetPartsAsync(fullPath, false).ConfigureAwait(false);
         if(container == null)
            return null;

         if(string.IsNullOrEmpty(path))
         {
            //looks like it's a container reference
            await container.FetchAttributesAsync().ConfigureAwait(false);
            return AzConvert.ToBlob(container);
         }
         else
         {
            CloudBlob blob = container.GetBlobReference(StoragePath.Normalize(path, false));
            if(!(await blob.ExistsAsync().ConfigureAwait(false)))
               return null;

            var r = new Blob(fullPath);
            await blob.FetchAttributesAsync().ConfigureAwait(false);
            AttachBlobMeta(r, blob);
            return r;
         }
      }

      internal static void AttachBlobMeta(Blob destination, CloudBlob source)
      {
         //ContentMD5 is base64-encoded hash, whereas we work with HEX encoded ones
         destination.MD5 = source.Properties.ContentMD5.Base64DecodeAsBytes().ToHexString();
         destination.Size = source.Properties.Length;
         destination.LastModificationTime = source.Properties.LastModified;

         string blobType = source.BlobType.ToString();
         if(blobType.EndsWith("Blob"))
            blobType = blobType.Substring(0, blobType.Length - 4).ToLower();

         destination.TryAddProperties(
            "BlobType", blobType,
            "IsDeleted", source.IsDeleted.ToString(),
            "IsSnapshot", source.IsSnapshot.ToString());

         if(source.Properties != null)
         {
            BlobProperties props = source.Properties;
            if(props.ContentDisposition != null)
               destination.Properties["ContentDisposition"] = props.ContentDisposition;
            if(props.ContentEncoding != null)
               destination.Properties["ContentEncoding"] = props.ContentEncoding;
            if(props.ContentLanguage != null)
               destination.Properties["ContentLanguage"] = props.ContentLanguage;
            if(props.ContentType != null)
               destination.Properties["ContentType"] = props.ContentType;
            if(props.ContentMD5 != null)
               destination.Properties["ContentMD5"] = props.ContentMD5;
            if(props.ETag != null)
               destination.Properties["ETag"] = props.ETag;
            destination.Properties["IsIncrementalCopy"] = props.IsIncrementalCopy.ToString();
            destination.Properties["IsServerEncrypted"] = props.IsServerEncrypted.ToString();
            destination.Properties["LeaseDuration"] = props.LeaseDuration.ToString();
            destination.Properties["LeaseState"] = props.LeaseState.ToString();
            destination.Properties["LeaseStatus"] = props.LeaseStatus.ToString();
            if(props.RehydrationStatus != null)
               destination.Properties["RehydrationStatus"] = props.RehydrationStatus.Value.ToString();
            if(props.RemainingDaysBeforePermanentDelete != null)
               destination.Properties["RemainingDaysBeforePermanentDelete"] = props.RemainingDaysBeforePermanentDelete.Value.ToString();
            if(props.StandardBlobTier != null)
               destination.Properties["StandardBlobTier"] = props.StandardBlobTier.ToString();
            if(props.PremiumPageBlobTier != null)
               destination.Properties["PremiumPageBlobTier"] = props.PremiumPageBlobTier.ToString();

         }

         if(source.Metadata?.Count > 0)
         {
            destination.Metadata.MergeRange(source.Metadata);
         }
      }

      private async Task AttachBlobMetaAsync(CloudBlob destination, Blob source)
      {
         if(source.Metadata == null)
            return;

         bool exists = await destination.ExistsAsync().ConfigureAwait(false);
         if(exists)
         {
            await destination.FetchAttributesAsync().ConfigureAwait(false);
         }

         destination.Metadata.Clear();

         foreach(KeyValuePair<string, string> pair in source.Metadata)
         {
            destination.Metadata[pair.Key] = pair.Value;
         }

         if(exists)
         {
            await destination.SetMetadataAsync().ConfigureAwait(false);
         }
      }

      public async Task<IReadOnlyCollection<Blob>> ListAsync(ListOptions options, CancellationToken cancellationToken = default)
      {
         if(options == null)
            options = new ListOptions();

         var result = new List<Blob>();
         var containers = new List<CloudBlobContainer>();

         if(StoragePath.IsRootPath(options.FolderPath) && _containerName == null)
         {
            // list all of the containers
            containers.AddRange(await GetCloudBlobContainersAsync(cancellationToken));

            //represent containers as folders in the result
            result.AddRange(containers.Select(AzConvert.ToBlob));

            if(!options.Recurse)
               return result;
         }
         else
         {
            (CloudBlobContainer container, string path) = await GetPartsAsync(options.FolderPath, false).ConfigureAwait(false);
            if(container == null)
               return new List<Blob>();
            options = options.Clone();
            options.FolderPath = path; //scan from subpath now
            containers.Add(container);
         }

         await Task.WhenAll(containers.Select(c => ListAsync(c, result, options, cancellationToken))).ConfigureAwait(false);

         if(options.MaxResults != null)
         {
            result = result.Take(options.MaxResults.Value).ToList();
         }

         return result;
      }

      private async Task ListAsync(CloudBlobContainer container,
         List<Blob> result,
         ListOptions options,
         CancellationToken cancellationToken)
      {
         using(var browser = new AzureBlobDirectoryBrowser(container, _containerName == null, BrowserParallelism))
         {
            IReadOnlyCollection<Blob> containerBlobs = await browser.ListFolderAsync(options, cancellationToken).ConfigureAwait(false);
            if(containerBlobs.Count > 0)
            {
               result.AddRange(containerBlobs);
            }
         }
      }

      private async Task<List<CloudBlobContainer>> GetCloudBlobContainersAsync(CancellationToken cancellationToken)
      {
         var result = new List<CloudBlobContainer>();

         ContainerResultSegment page = null;
         do
         {
            page = await _client.ListContainersSegmentedAsync(page?.ContinuationToken).ConfigureAwait(false);
            result.AddRange(page.Results);

         }
         while(page?.ContinuationToken != null);

         return result;
      }

      public async Task<Stream> OpenReadAsync(string fullPath, CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         (CloudBlobContainer container, string path) = await GetPartsAsync(fullPath, false);

         if(container == null)
            return null;

         CloudBlockBlob blob = container.GetBlockBlobReference(StoragePath.Normalize(path, false));

         try
         {
            return await blob.OpenReadAsync();
         }
         catch(AzureStorageException ex)
         {
            if(AzureStorageValidation.IsDoesntExist(ex))
               return null;

            if(!AzureStorageValidation.TryHandleStorageException(ex))
               throw;
         }

         throw new InvalidOperationException("must not be here");
      }

      public async Task<Stream> OpenRandomAccessReadAsync(string fullPath, CancellationToken cancellationToken = default)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         (CloudBlobContainer container, string path) = await GetPartsAsync(fullPath, false);

         if(container == null)
            return null;

         CloudBlockBlob blob = container.GetBlockBlobReference(StoragePath.Normalize(path, false));

         if(!(await blob.ExistsAsync()))
            return null;

         return new AzureBlockBlobRandomAccessStream(blob);
      }

      public Task<ITransaction> OpenTransactionAsync()
      {
         return Task.FromResult(EmptyTransaction.Instance);
      }

      public async Task<Stream> OpenWriteAsync(string fullPath, bool append, CancellationToken cancellationToken)
      {
         (CloudBlobContainer container, string path) = await GetPartsAsync(fullPath);

         if(append)
         {
            CloudAppendBlob cab = container.GetAppendBlobReference(path);
            await AttachBlobMetaAsync(cab, fullPath).ConfigureAwait(false);

            return await cab.OpenWriteAsync(!append).ConfigureAwait(false);
         }
         else
         {
            CloudBlockBlob cab = container.GetBlockBlobReference(path);
            await AttachBlobMetaAsync(cab, fullPath).ConfigureAwait(false);

            return await cab.OpenWriteAsync().ConfigureAwait(false);

         }
      }

      public async Task WriteAsync(string fullPath, Stream sourceStream, bool append, CancellationToken cancellationToken)
      {
         GenericValidation.CheckSourceStream(sourceStream);

         (CloudBlobContainer container, string path) = await GetPartsAsync(fullPath).ConfigureAwait(false);

         if(append)
         {
            CloudAppendBlob cab = container.GetAppendBlobReference(StoragePath.Normalize(path, false));
            if(!await cab.ExistsAsync())
               await cab.CreateOrReplaceAsync().ConfigureAwait(false);

            await cab.AppendFromStreamAsync(sourceStream).ConfigureAwait(false);

         }
         else
         {
            CloudBlockBlob cloudBlob = container.GetBlockBlobReference(StoragePath.Normalize(path, false));

            await cloudBlob.UploadFromStreamAsync(sourceStream).ConfigureAwait(false);
         }
      }


      #region [ Native Operations ] 

      public Task<string> GetStorageSasAsync(AccountSasPolicy accountPolicy, bool includeUrl)
      {
         if(accountPolicy is null)
            throw new ArgumentNullException(nameof(accountPolicy));

         if(_account == null)
            throw new NotSupportedException($"cannot create Shared Access Signature with current connection method");

         string sas = _account.GetSharedAccessSignature(accountPolicy.ToSharedAccessAccountPolicy());

         if(includeUrl)
         {
            sas = _account.BlobStorageUri.PrimaryUri + sas;
         }

         return Task.FromResult(sas);
      }

      public async Task<string> GetContainerSasAsync(string containerName, ContainerSasPolicy containerSasPolicy, bool includeUrl)
      {
         (CloudBlobContainer container, _) = await GetPartsAsync(containerName, true).ConfigureAwait(false);

         string sas = container.GetSharedAccessSignature(containerSasPolicy.ToSharedAccessBlobPolicy());

         if(includeUrl)
         {
            sas = container.Uri + sas;
         }

         return sas;
      }

      public async Task<ContainerPublicAccessType> GetContainerPublicAccessAsync(string containerName, CancellationToken cancellationToken = default)
      {
         (CloudBlobContainer container, _) = await GetPartsAsync(containerName, true).ConfigureAwait(false);

         BlobContainerPermissions perm = await container.GetPermissionsAsync(cancellationToken).ConfigureAwait(false);

         return (ContainerPublicAccessType)(int)perm.PublicAccess;
      }

      public async Task SetContainerPublicAccessAsync(string containerName, ContainerPublicAccessType containerPublicAccessType, CancellationToken cancellationToken = default)
      {
         (CloudBlobContainer container, _) = await GetPartsAsync(containerName, true).ConfigureAwait(false);

         BlobContainerPermissions perm = await container.GetPermissionsAsync(cancellationToken).ConfigureAwait(false);

         perm.PublicAccess = (BlobContainerPublicAccessType)(int)containerPublicAccessType;

         await container.SetPermissionsAsync(perm, cancellationToken).ConfigureAwait(false);
      }

      //todo: deprecate in favor of a more human readable signature
      public async Task<string> GetSasUriAsync(
         string fullPath,
         SharedAccessBlobPolicy sasConstraints,
         SharedAccessBlobHeaders headers,
         bool createContainer,
         CancellationToken cancellationToken)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         (CloudBlobContainer container, string path) = await GetPartsAsync(fullPath, createContainer);

         if(container == null)
            return null;

         CloudBlockBlob blob = container.GetBlockBlobReference(StoragePath.Normalize(path, false));

         try
         {
            return $@"{blob.Uri}{blob.GetSharedAccessSignature(sasConstraints, headers)}";
         }
         catch(AzureStorageException ex)
         {
            if(AzureStorageValidation.IsDoesntExist(ex))
               return null;

            if(!AzureStorageValidation.TryHandleStorageException(ex))
               throw;
         }

         throw new InvalidOperationException("must not be here");
      }

      public async Task<BlobLease> AcquireBlobLeaseAsync(
         string fullPath,
         TimeSpan maxLeaseTime,
         bool waitForRelease = false,
         CancellationToken cancellationToken = default)
      {
         (CloudBlobContainer container, string path) = await GetPartsAsync(fullPath);

         CloudBlockBlob leaseBlob = container.GetBlockBlobReference(path);

         //if blob doesn't exist, just create an empty one
         if(!(await leaseBlob.ExistsAsync()))
         {
            await WriteAsync(fullPath, new MemoryStream(), false, cancellationToken);
         }

         string leaseId = null;

         while(!cancellationToken.IsCancellationRequested)
         {
            try
            {
               leaseId = await leaseBlob.AcquireLeaseAsync(maxLeaseTime);

               break;
            }
            catch(AzureStorageException asx) when(asx.RequestInformation.HttpStatusCode == 409)
            {
               if(!waitForRelease)
               {
                  throw new StorageException(ErrorCode.Conflict, asx);
               }
               else
               {
                  await Task.Delay(TimeSpan.FromSeconds(1));
               }
            }
         }

         return new BlobLease(leaseBlob, leaseId);
      }

      #endregion

      #region [ Path forking ]

      private async Task<(CloudBlobContainer, string)> GetPartsAsync(string fullPath, bool createContainer = true)
      {
         GenericValidation.CheckBlobFullPath(fullPath);

         fullPath = StoragePath.Normalize(fullPath);
         if(fullPath == null)
            throw new ArgumentNullException(nameof(fullPath));

         string containerName, relativePath;

         if(_containerName == null)
         {
            int idx = fullPath.IndexOf(StoragePath.PathSeparator);
            if(idx == -1)
            {
               containerName = fullPath;
               relativePath = string.Empty;
            }
            else
            {
               containerName = fullPath.Substring(0, idx);
               relativePath = fullPath.Substring(idx + 1);
            }
         }
         else
         {
            containerName = _containerName;
            relativePath = fullPath;
         }

         if(!_containerNameToContainer.TryGetValue(containerName, out CloudBlobContainer container))
         {
            container = _client.GetContainerReference(containerName);
            if(_containerName == null)
            {
               if(!(await container.ExistsAsync().ConfigureAwait(false)))
               {
                  if(createContainer)
                  {
                     await container.CreateIfNotExistsAsync().ConfigureAwait(false);
                  }
                  else
                  {
                     return (null, null);
                  }
               }
            }

            _containerNameToContainer[containerName] = container;
         }

         return (container, relativePath);
      }

      #endregion
   }
}