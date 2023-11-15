using Azure;
using Azure.AI.OpenAI;
using Azure.Search.Documents;
using Azure.Search.Documents.Indexes;
using Azure.Search.Documents.Indexes.Models;
using Azure.Search.Documents.Models;
using Microsoft.Extensions.Configuration;

string query;
if (args.Length != 1)
{
    query = "Kann ich meine Diplomarbeit in Word schreiben?";
}
else
{
    query = args[0];
}

var configuration = new ConfigurationBuilder()
    .AddUserSecrets<Program>()
    .Build();

var serviceEndpoint = configuration["AZURE_SEARCH_SERVICE_ENDPOINT"] ?? string.Empty;
var indexName = configuration["AZURE_SEARCH_INDEX_NAME"] ?? string.Empty;
var key = configuration["AZURE_SEARCH_ADMIN_KEY"] ?? string.Empty;
var openaiApiKey = configuration["AZURE_OPENAI_API_KEY"] ?? string.Empty;
var openaiEndpoint = configuration["AZURE_OPENAI_ENDPOINT"] ?? string.Empty;
var embeddingsDeployment = configuration["AZURE_EMBEDINGS_DEPLOYMENT"] ?? string.Empty;
var chatGptDeployment = configuration["AZURE_CHATGPT_DEPLOYMENT"] ?? string.Empty;

// Initialize OpenAI client  
var credential = new AzureKeyCredential(openaiApiKey);
var openAIClient = new OpenAIClient(new Uri(openaiEndpoint), credential);

// Initialize Azure Cognitive Search clients  
var searchCredential = new AzureKeyCredential(key);
var indexClient = new SearchIndexClient(new Uri(serviceEndpoint), searchCredential);
var searchClient = indexClient.GetSearchClient(indexName);

var results = await SingleVectorSearch(searchClient, openAIClient, query);

var completionsOptions = new ChatCompletionsOptions()
{
    DeploymentName = chatGptDeployment,
    Messages =
    {
        new ChatMessage(ChatRole.System, $"""
            Du bist ein hilfreicher Assistent, der auf Basis einer Wissensdatenbank einer Schule
            den Schülerinnen und Schülern hilft, Fragen zu beantworten. Verwende bei der Beantwortung
            nur die Information, die in Folge im Bereich FAKTEN angegeben sind. Falls du eine
            Frage darauf basierend nicht beantworten kannst, sage "Das weiß ich leider nicht".

            FAKTEN

            {string.Join("\n\n", results)}
            """),
        new ChatMessage(ChatRole.User, query),
    }
};

var response = await openAIClient.GetChatCompletionsStreamingAsync(completionsOptions);
await foreach (var update in response)
{
    if (!string.IsNullOrEmpty(update.ContentUpdate))
    {
        Console.Write(update.ContentUpdate);
    }
}

Console.WriteLine();

async Task<List<string>> SingleVectorSearch(SearchClient searchClient, OpenAIClient openAIClient, string query, int k = 3)
{
    // Generate the embedding for the query  
    var queryEmbeddings = await GenerateEmbeddings(query, openAIClient);

    // Perform the vector similarity search  
    var searchOptions = new SearchOptions
    {
        VectorSearch = new()
        {
            Queries =
            {
                new VectorizedQuery(queryEmbeddings)
                {
                    Fields = { "contentVector" }, Exhaustive = true, KNearestNeighborsCount = 3
                }
            },
        }
    };

    SearchResults<SearchDocument> response = await searchClient.SearchAsync<SearchDocument>(null, searchOptions);

    List<string> searchResults = [];
    await foreach (SearchResult<SearchDocument> result in response.GetResultsAsync())
    {
        if (result.Document.TryGetValue("content", out var content) && content != null)
        {
            searchResults.Add(content.ToString()!);
        }
    }

    return searchResults;
}

async Task<float[]> GenerateEmbeddings(string text, OpenAIClient openAIClient)
{
    var response = await openAIClient.GetEmbeddingsAsync(new EmbeddingsOptions(embeddingsDeployment, [text]));
    return response.Value.Data[0].Embedding.ToArray();
}