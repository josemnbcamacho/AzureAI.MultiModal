using Azure;

using Azure.Storage.Blobs;
using Azure.Storage.Sas;

using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Models;

using System.Text;
using System.Text.Json;
using System.Net.Http;

public class AiVisionHelper
{

    private string _apiKeyVision = "";
    private string _endPointVision = "";

    private string _apiKeySearch = "";
    private string _endPointSearch = "";

    private string _connectionStringStorage = "";

    private string _textEmbeddingUrl = @"{AR_VISION_ENDPOINT}/computervision/retrieval:vectorizeText?api-version=2024-02-01&model-version=2023-04-15";
    private string _imageEmbeddingUrl = @"{AR_VISION_ENDPOINT}/computervision/retrieval:vectorizeImage?api-version=2024-02-01&model-version=2023-04-15";
    private string _imageEmbeddingPayload = @"{""url"":""{IMAGE_URL}""}";

    SearchIndexClient searchIndexClient;


    public AiVisionHelper(
        string apiKeyVision, string endPointVision,
        string apiKeySearch, string endpointSearch,
        string connectionStringStorage)
    {
        _apiKeyVision = apiKeyVision;
        _endPointVision = endPointVision;

        _apiKeySearch = apiKeySearch;
        _endPointSearch = endpointSearch;

        _connectionStringStorage = connectionStringStorage;

        AzureKeyCredential searchCredential = new AzureKeyCredential(apiKeySearch);
        searchIndexClient = new SearchIndexClient(new Uri(endpointSearch), searchCredential);

    }

    public async Task<float[]> GetTextEmbedding(string text)
    {
        //Compose API url & request payload
        string url = _textEmbeddingUrl.Replace("{AR_VISION_ENDPOINT}", _endPointVision);
        string requestPayload = $"{{\"text\":\"{text}\"}}";

        return await GetEmbedding(url, requestPayload);
    }

    public async Task<float[]> GetImageEmbedding(string imageUrl)
    {
        //Compose API url & request payload
        string url = _imageEmbeddingUrl.Replace("{AR_VISION_ENDPOINT}", _endPointVision);
        string requestPayload = _imageEmbeddingPayload.Replace("{IMAGE_URL}", imageUrl);

        return await GetEmbedding(url, requestPayload);
    }

    private async Task<float[]> GetEmbedding(string url, string requestPayload)
    {
        //Post request to url
        HttpClient httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Ocp-Apim-Subscription-Key", _apiKeyVision);
        HttpContent httpContent = new StringContent(requestPayload, Encoding.UTF8, "application/json");

        HttpRequestMessage httpRequestMessage = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = httpContent
        };
        HttpResponseMessage httpResponseMessage = await httpClient.SendAsync(httpRequestMessage);

        //Check if request was successful
        if (!httpResponseMessage.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error: {httpResponseMessage.StatusCode}");
            return new float[0];
        }

        //Parse response
        JsonDocument jsonDocument = JsonDocument.Parse(
            await httpResponseMessage.Content.ReadAsStringAsync()
        );

        return jsonDocument
                .RootElement.GetProperty("vector")
                .EnumerateArray()
                .Select(element => element.GetSingle())
                .ToArray();
    }

    public async Task<Uri> UploadLocalFile(string localFilePath, string containerName, string blobName)
    {
        //Create blob client
        BlobServiceClient blobServiceClient = new BlobServiceClient(_connectionStringStorage);
        BlobContainerClient blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);
        BlobClient blobClient = blobContainerClient.GetBlobClient(blobName);

        //Check if container exists and if not create it
        if (!await blobContainerClient.ExistsAsync())
        {
            await blobContainerClient.CreateAsync();
        }

        //Upload file
        using FileStream fileStream = File.OpenRead(localFilePath);
        await blobClient.UploadAsync(fileStream, true);

        //Create SAS token
        BlobSasBuilder blobSasBuilder = new BlobSasBuilder()
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b"
        };
        blobSasBuilder.SetPermissions(BlobSasPermissions.Read);
        blobSasBuilder.ExpiresOn = DateTimeOffset.UtcNow.AddHours(1);
        Uri sasUri = blobClient.GenerateSasUri(blobSasBuilder);

        return sasUri;

    }

    public async Task<Uri> GetBlobSharedUrl(string blobUrl)
    {
        // Parse the blobUrl to get the container name and blob name
        Uri blobUri = new Uri(blobUrl);
        string containerName = blobUri.Segments[1].TrimEnd('/');
        string blobName = string.Join("", blobUri.Segments.Skip(2));

        // Create blob client
        BlobServiceClient blobServiceClient = new BlobServiceClient(_connectionStringStorage);
        BlobContainerClient blobContainerClient = blobServiceClient.GetBlobContainerClient(containerName);
        BlobClient blobClient = blobContainerClient.GetBlobClient(blobName);

        // Generate SAS token for read access
        BlobSasBuilder blobSasBuilder = new BlobSasBuilder()
        {
            BlobContainerName = containerName,
            BlobName = blobName,
            Resource = "b"
        };
        blobSasBuilder.SetPermissions(BlobSasPermissions.Read);
        blobSasBuilder.ExpiresOn = DateTimeOffset.UtcNow.AddHours(1);
        Uri sasUri = blobClient.GenerateSasUri(blobSasBuilder);

        return sasUri;
    }

    public async Task<bool> CreateSearchIndex(string indexName)
    {

        string searchConfigName = "fact-config";

        int modelDimensions = 1024;
        SearchIndex searchIndex = new(indexName)
        {
            Fields =
            {
                new SimpleField("image_parent_id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true, IsSortable = true, IsFacetable = true },
                new SearchableField("title") { IsFilterable = true },
                new SearchField("image_vector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = modelDimensions,
                    VectorSearchConfiguration = searchConfigName
                },
            },
            VectorSearch = new()
            {
                AlgorithmConfigurations =
                {
                    new HnswVectorSearchAlgorithmConfiguration(searchConfigName)
                }
            }
        };

        try { await searchIndexClient.DeleteIndexAsync(indexName); } catch { }
        await searchIndexClient.CreateIndexAsync(searchIndex);

        return true;
    }

    public async Task<bool> StoreImageEmbedding(string indexName, List<ImageEmbedding> imageEmbeddings)
    {
        SearchClient searchClient = searchIndexClient.GetSearchClient(indexName);
        await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(imageEmbeddings));
        return true;
    }

    internal async Task<List<ImageEmbedding>> QuerySearchIndex(string indexName, float[] queryVector)
    {
        SearchClient searchClient = searchIndexClient.GetSearchClient(indexName);

        SearchResults<ImageEmbedding> response = await searchClient.SearchAsync<ImageEmbedding>(
            null,
            new SearchOptions
            {
                Vectors = { new() { Value = queryVector, KNearestNeighborsCount = 5, Fields = { "image_vector" } } },
            }
        );

        List<ImageEmbedding> results = new List<ImageEmbedding>();
        await foreach (SearchResult<ImageEmbedding> result in response.GetResultsAsync())
        {
            results.Add(result.Document);
        }

        return results;
    }
}

public class ImageEmbedding
{
    public string image_parent_id { get; set; } = "";
    public string title { get; set; } = "";
    public float[] image_vector { get; set; } = new float[1024];

    public string metadata_storage_path { get; set; } = "";
}

