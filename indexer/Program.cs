using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

var serviceEndpoint = configuration["AZURE_SEARCH_SERVICE_ENDPOINT"] ?? string.Empty;
var indexName = configuration["AZURE_SEARCH_INDEX_NAME"] ?? string.Empty;
var key = configuration["AZURE_SEARCH_ADMIN_KEY"] ?? string.Empty;
var openaiApiKey = configuration["AZURE_OPENAI_API_KEY"] ?? string.Empty;
var openaiEndpoint = configuration["AZURE_OPENAI_ENDPOINT"] ?? string.Empty;
var embeddingsDeployment = configuration["AZURE_EMBEDINGS_DEPLOYMENT"] ?? string.Empty;
const string SemanticSearchConfigName = "my-semantic-config";
const int ModelDimensions = 1536;

// Initialize OpenAI client  
var credential = new AzureKeyCredential(openaiApiKey);
var openAIClient = new OpenAIClient(new Uri(openaiEndpoint), credential);

// Initialize Azure Cognitive Search clients  
var searchCredential = new AzureKeyCredential(key);
var indexClient = new SearchIndexClient(new Uri(serviceEndpoint), searchCredential);
var searchClient = indexClient.GetSearchClient(indexName);

// Create the search index  
await indexClient.DeleteIndexAsync(indexName);
await indexClient.CreateOrUpdateIndexAsync(GetSampleIndex(indexName));

var sampleDocuments = await GetSampleDocumentsAsync(openAIClient);
await searchClient.IndexDocumentsAsync(IndexDocumentsBatch.Upload(sampleDocuments));

static SearchIndex GetSampleIndex(string name)
{
    string vectorSearchProfile = "my-vector-profile";
    string vectorSearchHnswConfig = "my-hnsw-vector-config";

    SearchIndex searchIndex = new(name)
    {
        VectorSearch = new()
        {
            Profiles =
                {
                    new VectorSearchProfile(vectorSearchProfile, vectorSearchHnswConfig)
                },
            Algorithms =
                {
                    new HnswAlgorithmConfiguration(vectorSearchHnswConfig)
                }
        },
        SemanticSearch = new()
        {

            Configurations =
                    {
                       new SemanticConfiguration(SemanticSearchConfigName, new()
                       {
                           TitleField = new("title"),
                           ContentFields =
                           {
                               new("content")
                           },
                       })

                },
        },
        Fields =
            {
                new SimpleField("id", SearchFieldDataType.String) { IsKey = true, IsFilterable = true, IsSortable = true, IsFacetable = true },
                new SearchableField("title") { IsFilterable = true, IsSortable = true },
                new SearchableField("content") { IsFilterable = true },
                new SearchField("titleVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = ModelDimensions,
                    VectorSearchProfileName = vectorSearchProfile
                },
                new SearchField("contentVector", SearchFieldDataType.Collection(SearchFieldDataType.Single))
                {
                    IsSearchable = true,
                    VectorSearchDimensions = ModelDimensions,
                    VectorSearchProfileName = vectorSearchProfile
                }
            }
    };

    return searchIndex;
}

async Task<List<SearchDocument>> GetSampleDocumentsAsync(OpenAIClient openAIClient)
{
    List<SearchDocument> sampleDocuments = [];

    var markdownFiles = Directory.GetFiles(Path.Combine("..", "data"), "*.md");
    foreach (var file in markdownFiles)
    {
        var fileName = Path.GetFileNameWithoutExtension(file);
        if (fileName.StartsWith("summary") || fileName.StartsWith("archive")) { continue; }

        var content = await File.ReadAllTextAsync(file);
        var title = content.Split("\n")[0].Replace("#", string.Empty).Trim();

        float[] titleEmbeddings, contentEmbeddings;
        try
        {
            titleEmbeddings = await GenerateEmbeddings(title, openAIClient);
            contentEmbeddings = await GenerateEmbeddings(content, openAIClient);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error generating embeddings for {title}: {ex.Message}");
            continue;
        }

        var document = new Dictionary<string, object>
        {
            ["id"] = fileName,
            ["title"] = title,
            ["content"] = content,
            ["titleVector"] = titleEmbeddings,
            ["contentVector"] = contentEmbeddings
        };
        sampleDocuments.Add(new SearchDocument(document));
    }

    return sampleDocuments;
}

async Task<float[]> GenerateEmbeddings(string text, OpenAIClient openAIClient)
{
    var response = await openAIClient.GetEmbeddingsAsync(new EmbeddingsOptions(embeddingsDeployment, [text]));
    return response.Value.Data[0].Embedding.ToArray();
}