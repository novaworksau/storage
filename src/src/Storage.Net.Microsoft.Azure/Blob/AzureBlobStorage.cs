﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Storage.Net.Blob;
using Microsoft.WindowsAzure.Storage.Blob;
using AzureStorageException = Microsoft.WindowsAzure.Storage.StorageException;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;
using System.Threading.Tasks;

namespace Storage.Net.Microsoft.Azure.Blob
{
   /// <summary>
   /// Azure Blob Storage
   /// </summary>
   public class AzureBlobStorage : AsyncBlobStorage
   {
      private readonly CloudBlobContainer _blobContainer;

      /// <summary>
      /// Creates an instance from account name, private key and container name
      /// </summary>
      public AzureBlobStorage(string accountName, string key, string containerName)
      {
         ValidateContainerName(containerName);

         var account = new CloudStorageAccount(
            new StorageCredentials(accountName, key),
            true);

         CloudBlobClient blobClient = account.CreateCloudBlobClient();

         _blobContainer = blobClient.GetContainerReference(containerName);
         _blobContainer.CreateIfNotExistsAsync().Wait();
      }

      /// <summary>
      /// Creates and instance from network credential and container name
      /// </summary>
      /// <param name="credential"></param>
      /// <param name="containerName"></param>
      public AzureBlobStorage(NetworkCredential credential, string containerName) :
         this(credential.UserName, credential.Password, containerName)
      {

      }

      /// <summary>
      /// Creates an insance from connection string
      /// </summary>
      public AzureBlobStorage(string connectionString, string containerName)
      {
         if (connectionString == null) throw new ArgumentNullException(nameof(connectionString));
         ValidateContainerName(containerName);

         CloudStorageAccount account;
         if(!CloudStorageAccount.TryParse(connectionString, out account))
         {
            throw new ArgumentException("could not parse provided connection string");
         }

         CloudBlobClient blobClient = account.CreateCloudBlobClient();

         _blobContainer = blobClient.GetContainerReference(containerName);
         _blobContainer.CreateIfNotExistsAsync().Wait();
      }

      private void ValidateContainerName(string containerName)
      {
         if (containerName == null) throw new ArgumentNullException(nameof(containerName));

         /* from MSDN:
          *
          A container name must be a valid DNS name, conforming to the following naming rules:
          1. Container names must start with a letter or number, and can contain only letters, numbers, and the dash (-) character.
          2. Every dash (-) character must be immediately preceded and followed by a letter or number; consecutive dashes are not permitted in container names.
          3. All letters in a container name must be lowercase.
          4. Container names must be from 3 through 63 characters long.
         */

         //1. todo

         //2. todo

         //3. check that all characters are lowercase
         for (int i = 0; i < containerName.Length; i++)
         {
            if (char.IsLetter(containerName[i]) && !char.IsLower(containerName, i))
            {
               throw new ArgumentOutOfRangeException(nameof(containerName),
                  $"container [{containerName}] has uppercase character at position {i}");
            }
         }

         //4. check for length
         if (containerName.Length < 3 || containerName.Length > 63)
         {
            throw new ArgumentOutOfRangeException(nameof(containerName),
               $"container [{containerName}] length must be between 3 and 63 but it's {containerName.Length}");
         }
      }

      /// <summary>
      /// Gets all the blob names, then filters by prefix optionally
      /// </summary>
      public override async Task<IEnumerable<string>> ListAsync(string prefix)
      {
         GenericValidation.CheckBlobPrefix(prefix);

         var result = new List<string>();

         BlobContinuationToken token = null;

         do
         {
            BlobResultSegment segment = await _blobContainer.ListBlobsSegmentedAsync(prefix, true, BlobListingDetails.None, null, token, null, null);

            foreach (CloudBlockBlob blob in segment.Results.OfType<CloudBlockBlob>())
            {
               result.Add(ToUserId(blob.Name));
            }

         }
         while (token != null);

         return result;
      }

      /// <summary>
      /// Deletes blob remotely
      /// </summary>
      public override async Task DeleteAsync(string id)
      {
         GenericValidation.CheckBlobId(id);

         id = ToInternalId(id);

         CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(id);
         await blob.DeleteIfExistsAsync();
      }

      /// <summary>
      /// Uploads from stream
      /// </summary>
      public override async Task UploadFromStreamAsync(string id, Stream sourceStream)
      {
         GenericValidation.CheckBlobId(id);
         if (sourceStream == null) throw new ArgumentNullException(nameof(sourceStream));
         id = ToInternalId(id);

         CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(id);
         await blob.UploadFromStreamAsync(sourceStream);
      }

      /// <summary>
      /// Appends to the append blob.
      /// </summary>
      public override async Task AppendFromStreamAsync(string id, Stream chunkStream)
      {
         GenericValidation.CheckBlobId(id);
         if (chunkStream == null) throw new ArgumentNullException(nameof(chunkStream));
         id = ToInternalId(id);

         CloudAppendBlob cab = _blobContainer.GetAppendBlobReference(id);
         if (!cab.ExistsAsync().Result) await cab.CreateOrReplaceAsync();

         await cab.AppendBlockAsync(chunkStream);
      }

      /// <summary>
      /// Downloads to stream
      /// </summary>
      public override async Task DownloadToStreamAsync(string id, Stream targetStream)
      {
         GenericValidation.CheckBlobId(id);
         if (targetStream == null) throw new ArgumentNullException(nameof(targetStream));
         id = ToInternalId(id);

         CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(id);

         try
         {
            await blob.DownloadToStreamAsync(targetStream);
         }
         catch(AzureStorageException ex)
         {
            if(!TryHandleStorageException(ex)) throw;
         }
      }

      /// <summary>
      /// Opens stream to read
      /// </summary>
      /// <param name="id"></param>
      /// <returns></returns>
      public override async Task<Stream> OpenStreamToReadAsync(string id)
      {
         GenericValidation.CheckBlobId(id);

         id = ToInternalId(id);

         CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(id);
         return await blob.OpenReadAsync();
      }

      /// <summary>
      /// Checks if the blob exists by trying to fetch attributes from the blob reference and checkign if that fails
      /// </summary>
      public override async Task<bool> ExistsAsync(string id)
      {
         GenericValidation.CheckBlobId(id);
         CloudBlockBlob blob = _blobContainer.GetBlockBlobReference(ToInternalId(id));
         return await blob.ExistsAsync();
      }

      /// <summary>
      /// Gets blob metadata
      /// </summary>
      public override async Task<BlobMeta> GetMetaAsync(string id)
      {
         GenericValidation.CheckBlobId(id);

         CloudBlob blob = _blobContainer.GetBlobReference(id);
         if (!(await blob.ExistsAsync())) return null;

         await blob.FetchAttributesAsync();

         return new BlobMeta(
            blob.Properties.Length);
      }

      private static string ToInternalId(string userId)
      {
         return userId.UrlEncode();
      }

      private static string ToUserId(string internalId)
      {
         return internalId.UrlDecode();
      }

      private static bool TryHandleStorageException(AzureStorageException ex)
      {
#if NETFULL
         WebException wex = ex.InnerException as WebException;
         if(wex != null)
         {
            var response = wex.Response as HttpWebResponse;
            if(response != null)
            {
               if(response.StatusCode == HttpStatusCode.NotFound)
               {
                  throw new StorageException(ErrorCode.NotFound, ex);
               }
            }
         }
#endif

         return false;
      }
   }
}
