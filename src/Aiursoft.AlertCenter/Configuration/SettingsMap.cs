using Aiursoft.AlertCenter.Models;

namespace Aiursoft.AlertCenter.Configuration;

public class SettingsMap
{
    public const string ProjectName = "ProjectName";
    public const string BrandName = "BrandName";
    public const string BrandHomeUrl = "BrandHomeUrl";
    public const string ProjectLogo = "ProjectLogo";
    public const string AllowUserAdjustNickname = "Allow_User_Adjust_Nickname";
    public const string Icp = "Icp";
    public const string DummyNumber = "DummyNumber";
    public const string DummyChoice = "DummyChoice";

    public const string EmbeddingOllamaInstance = "EmbeddingOllamaInstance";
    public const string EmbeddingModel = "EmbeddingModel";
    public const string EmbeddingApiToken = "EmbeddingApiToken";
    public const string EnableEmbeddingBasedSearch = "EnableEmbeddingBasedSearch";
    public const string EmbeddingQueryCacheLimit = "EmbeddingQueryCacheLimit";

    public class FakeLocalizer
    {
        public string this[string name] => name;
    }

    private static readonly FakeLocalizer Localizer = new();

    public static readonly List<GlobalSettingDefinition> Definitions = new()
    {
        new GlobalSettingDefinition
        {
            Key = ProjectName,
            Name = Localizer["Project Name"],
            Description = Localizer["The name of the project displayed in the frontend."],
            Type = SettingType.Text,
            DefaultValue = "Aiursoft Template"
        },
        new GlobalSettingDefinition
        {
            Key = BrandName,
            Name = Localizer["Brand Name"],
            Description = Localizer["The brand name displayed in the footer."],
            Type = SettingType.Text,
            DefaultValue = "Aiursoft"
        },
        new GlobalSettingDefinition
        {
            Key = BrandHomeUrl,
            Name = Localizer["Brand Home URL"],
            Description = Localizer[" The link to the brand's home page."],
            Type = SettingType.Text,
            DefaultValue = "https://www.aiursoft.com/"
        },
        new GlobalSettingDefinition
        {
            Key = ProjectLogo,
            Name = Localizer["Project Logo"],
            Description = Localizer["The logo of the project displayed in the navbar and footer. Support jpg, png, svg."],
            Type = SettingType.File,
            DefaultValue = "",
            Subfolder = "project-logo",
            AllowedExtensions = "jpg png svg",
            MaxSizeInMb = 5
        },
        new GlobalSettingDefinition
        {
            Key = AllowUserAdjustNickname,
            Name = Localizer["Allow User Adjust Nickname"],
            Description = Localizer["Allow users to adjust their nickname in the profile management page."],
            Type = SettingType.Bool,
            DefaultValue = "True"
        },
        new GlobalSettingDefinition
        {
            Key = Icp,
            Name = Localizer["ICP Number"],
            Description = Localizer["The ICP license number for China mainland users. Leave empty to hide."],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingOllamaInstance,
            Name = Localizer["Embedding Ollama Instance"],
            Description = Localizer["The Ollama API base URL used specifically for generating incident and query embeddings (vector search). Only the host is used — /api/embed is appended automatically. E.g. https://ollama.example.com"],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingModel,
            Name = Localizer["Embedding Model"],
            Description = Localizer["The embedding model name, e.g. bge-m3:latest. Must be available at the Embedding Ollama Instance. Only used for vector search."],
            Type = SettingType.Text,
            DefaultValue = "bge-m3:latest"
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingApiToken,
            // AlertCenter 支持加密的 Secret 设置，用于保护嵌入服务令牌。
            Name = Localizer["Embedding API Token"],
            Description = Localizer["The bearer token for authenticating with the Embedding Ollama Instance, e.g. 5a0fbdefa19f...."],
            Type = SettingType.Secret,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = EnableEmbeddingBasedSearch,
            Name = Localizer["Enable Embedding-Based Search"],
            Description = Localizer["Master switch for semantic (vector-based) incident search. When enabled and all dependencies are configured, search results display a green \"Search based on AI (Vector Database)\" badge and use cosine similarity ranking via the embedding model. When disabled, or when dependencies are missing, search silently falls back to keyword matching without the badge. Requires Embedding Ollama Instance and Embedding Model."],
            Type = SettingType.Bool,
            DefaultValue = "False"
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingQueryCacheLimit,
            Name = Localizer["Embedding Query Cache Limit"],
            Description = Localizer["Maximum number of cached query embeddings in the database. When exceeded, the least recently accessed entries are evicted (LRU). Default 2000. Adjust based on your database capacity and expected query diversity."],
            Type = SettingType.Number,
            DefaultValue = "2000"
        },
        new GlobalSettingDefinition
        {
            Key = DummyNumber,
            Name = Localizer["Dummy Number"],
            Description = Localizer["A dummy number for testing."],
            Type = SettingType.Number,
            DefaultValue = "0"
        },
        new GlobalSettingDefinition
        {
            Key = DummyChoice,
            Name = Localizer["Dummy Choice"],
            Description = Localizer["A dummy choice for testing."],
            Type = SettingType.Choice,
            DefaultValue = "A",
            ChoiceOptions = new Dictionary<string, string>
            {
                { "A", "Option A" },
                { "B", "Option B" }
            }
        }
    };
}
