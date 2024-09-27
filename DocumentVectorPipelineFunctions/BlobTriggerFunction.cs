using System.ClientModel;
using System.Data;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Azure;
using Azure.AI.FormRecognizer.DocumentAnalysis;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Dapper;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenAI.Embeddings;

namespace DocumentVectorPipelineFunctions;

public class BlobTriggerFunction(
    IConfiguration configuration,
    DocumentAnalysisClient documentAnalysisClient,
    ILoggerFactory loggerFactory,
    EmbeddingClient embeddingClient)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<BlobTriggerFunction>();

    private const string AzureOpenAIModelDeploymentDimensionsName = "AzureOpenAIModelDimensions";
    private const string SqlConnectionString = "SqlConnectionString";
    private const string CreateDocumentTableScript = @"
        IF NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_NAME = 'document')
        BEGIN
            CREATE TABLE [dbo].[document] (
                [Id] INT IDENTITY(1,1) PRIMARY KEY NOT NULL,
                [ChunkId] INT NULL,
                [DocumentUrl] VARCHAR(500) NULL,
                [Embedding] VARBINARY(8000) NULL,
                [ChunkText] VARCHAR(MAX) NULL,
                [PageNumber] INT NULL
            );
        END";

    string? managedIdentityClientId = Environment.GetEnvironmentVariable("AzureManagedIdentityClientId");
    DefaultAzureCredential? credential;
    private static readonly int DefaultDimensions = 1536;

    private const int MaxRetryCount = 100;
    private const int RetryDelay = 10 * 1000; // 10 seconds

    private const int MaxBatchSize = 10;
    private const int MaxDegreeOfParallelism = 50;

    private int embeddingDimensions = DefaultDimensions;

    [Function("BlobTriggerFunction")]
    public async Task Run([BlobTrigger("documents/{name}", Connection = "AzureBlobStorageAccConnectionString")] BlobClient blobClient)
    {
        this._logger.LogInformation("Starting processing of blob name: '{name}'", blobClient.Name);

        if (await blobClient.ExistsAsync())
        {
            await this.HandleBlobCreateEventAsync(blobClient);
        }
        else
        {
            await this.HandleBlobDeleteEventAsync(blobClient);
        }
        this._logger.LogInformation("Finished processing of blob name: '{name}'", blobClient.Name);
    }

    private async Task HandleBlobCreateEventAsync(BlobClient blobClient)
    {
        this.embeddingDimensions = configuration.GetValue<int>(AzureOpenAIModelDeploymentDimensionsName, DefaultDimensions);
        var connstring = configuration.GetValue<string>(SqlConnectionString);
        this._logger.LogInformation("Using OpenAI model dimensions: '{embeddingDimensions}'.", this.embeddingDimensions);

        this._logger.LogInformation("Analyzing document using DocumentAnalyzerService from blobUri: '{blobUri}' using layout: {layout}", blobClient.Name, "prebuilt-read");

        using MemoryStream memoryStream = new MemoryStream();
        await blobClient.DownloadToAsync(memoryStream);
        memoryStream.Seek(0, SeekOrigin.Begin);

        var operation = await documentAnalysisClient.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            "prebuilt-read",
            memoryStream);

        var result = operation.Value;
        this._logger.LogInformation("Extracted content from '{name}', # pages {pageCount}", blobClient.Name, result.Pages.Count);

        var textChunks = TextChunker.FixedSizeChunking(result);

        var listOfBatches = new List<List<TextChunk>>();

        int totalChunksCount = 0;
        var batchChunkTexts = new List<TextChunk>(MaxBatchSize);
        for (int i = 0; i <= textChunks.Count(); i++)
        {
            if (i == textChunks.Count())
            {
                if (batchChunkTexts.Count() > 0)
                {
                    listOfBatches.Add(new List<TextChunk>(batchChunkTexts));
                }
                batchChunkTexts.Clear();

                break;
            }

            batchChunkTexts.Add(textChunks.ElementAt(i));
            totalChunksCount++;

            if (batchChunkTexts.Count >= MaxBatchSize)
            {
                listOfBatches.Add(new List<TextChunk>(batchChunkTexts));
                batchChunkTexts.Clear();
            }
        }

        this._logger.LogInformation("Processing list of batches in parallel, total batches: {listSize}, chunks count: {chunksCount}", listOfBatches.Count(), totalChunksCount);

        //check if database table exists if not create one 

        this._logger.LogInformation("Create document table if it doesnot exist.");    

        if (managedIdentityClientId != null)
        {
            //User-assigned managed identity Client ID is passed in via ManagedIdentityClientId
            var defaultCredentialOptions = new DefaultAzureCredentialOptions { ManagedIdentityClientId = managedIdentityClientId };
            credential = new DefaultAzureCredential(defaultCredentialOptions);
        }
        else
        {
            //System-assigned managed identity or logged-in identity of Visual Studio, Visual Studio Code, Azure CLI or Azure PowerShell
            credential = new DefaultAzureCredential();
        }

        var token = credential.GetToken(new Azure.Core.TokenRequestContext(new[] { "https://database.windows.net/.default" }));

        await EnsureDocumentTableExistsAsync(connstring,token, CreateDocumentTableScript);

        await Parallel.ForEachAsync(listOfBatches, new ParallelOptions { MaxDegreeOfParallelism = MaxDegreeOfParallelism }, async (batchChunkTexts, cancellationToken) =>
        {
            this._logger.LogInformation("Processing batch of size: {batchSize}", batchChunkTexts.Count());
       

            this._logger.LogInformation("Processing text: {0}", batchChunkTexts);

            this._logger.LogInformation("Generating embeddings for batch of size: '{size}'.", batchChunkTexts.Count());

            if (batchChunkTexts.Count() > 0)
            {
                var embeddings = await this.GenerateEmbeddingsWithRetryAsync(batchChunkTexts);
                this._logger.LogInformation("embeddings generated {0}", embeddings);

                if (embeddings.Count() > 0)
                {
                    // Save into Azure SQL
                    this._logger.LogInformation("Begin Saving data in Azure SQL");

                    for (int index = 0; index < batchChunkTexts.Count; index++)
                    {
                        using (var connection = new SqlConnection(connstring))
                        {
                            string insertQuery = @"INSERT INTO [dbo].[Document] (ChunkId, DocumentUrl, Embedding, ChunkText, PageNumber) VALUES (@ChunkId, @DocumentUrl,JSON_ARRAY_TO_VECTOR(@Embedding),@ChunkText, @PageNumber);";

                            var doc = new Document()
                            {
                                ChunkId = batchChunkTexts[index].ChunkNumber,
                                DocumentUrl = blobClient.Uri.AbsoluteUri,
                                //Embedding = (embeddings[index].Vector.ToArray()).Select(f => BitConverter.GetBytes(f)).SelectMany(b => b).ToArray(),
                                Embedding = JsonSerializer.Serialize(embeddings[index].Vector),
                                ChunkText = batchChunkTexts[index].Text,
                                PageNumber = batchChunkTexts[index].PageNumberIfKnown,
                            };                           
                            connection.AccessToken = token.Token;
                            var result = connection.Execute(insertQuery, doc);
                        }                                    
                    }

                    this._logger.LogInformation("End Saving data in Azure SQL");
                }
            }            
        });

        this._logger.LogInformation("Finished processing blob {name}, total chunks processed {count}.", blobClient.Name, totalChunksCount);
    }

    private async Task<EmbeddingCollection> GenerateEmbeddingsWithRetryAsync(IEnumerable<TextChunk> batchChunkTexts)
    {
        EmbeddingGenerationOptions embeddingGenerationOptions = new EmbeddingGenerationOptions()
        {
            Dimensions = this.embeddingDimensions
        };

        int retryCount = 0;
        while (retryCount < MaxRetryCount)
        {
            try
            {
                return await embeddingClient.GenerateEmbeddingsAsync(batchChunkTexts.Select(p => p.Text).ToList(), embeddingGenerationOptions);
            }
            catch (ClientResultException ex)
            {
                if (ex.Status is ((int)HttpStatusCode.TooManyRequests) or ((int)HttpStatusCode.Unauthorized))
                {
                    if (retryCount >= MaxRetryCount)
                    {
                        throw new Exception($"Max retry attempts reached generating embeddings with exception: {ex}.");
                    }

                    retryCount++;

                    await Task.Delay(RetryDelay);
                }
                else
                {
                    throw new Exception($"Failed to generate embeddings with error: {ex}.");
                }
            }
        }

        throw new Exception($"Failed to generate embeddings after retrying for ${MaxRetryCount} times.");
    }

    private async Task HandleBlobDeleteEventAsync(BlobClient blobClient)
    {
        // TODO (amisi) - Implement me.
        this._logger.LogInformation("Handling delete event for blob name {name}.", blobClient.Name);

        await Task.Delay(1);
    }

    private static async Task EnsureDocumentTableExistsAsync(string connectionString,AccessToken token, string script)
    {
        using (var connection = new SqlConnection(connectionString))
        {
            connection.AccessToken = token.Token;
            await connection.ExecuteAsync(script);
        }
    }
}
