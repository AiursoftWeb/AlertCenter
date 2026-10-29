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
            Key = EnableEmbeddingBasedSearch,
            Name = Localizer["Enable semantic search"],
            Description = Localizer["Use embeddings to search incidents. Falls back to keywords when unavailable."],
            Type = SettingType.Bool,
            DefaultValue = "False"
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingOllamaInstance,
            Name = Localizer["Embedding server URL"],
            Description = Localizer["Base URL of an Ollama-compatible embedding server. Incident titles, content and application names are sent to this server."],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingModel,
            Name = Localizer["Embedding model"],
            Description = Localizer["Model installed on the embedding server. Changing the model or server regenerates the index."],
            Type = SettingType.Text,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingApiToken,
            Name = Localizer["Embedding API token"],
            Description = Localizer["Optional bearer token for the embedding server."],
            Type = SettingType.Secret,
            DefaultValue = ""
        },
        new GlobalSettingDefinition
        {
            Key = EmbeddingQueryCacheLimit,
            Name = Localizer["Query embedding cache limit"],
            Description = Localizer["Maximum cached query vectors (1–10000). Invalid values use 2000."],
            Type = SettingType.Number,
            DefaultValue = "2000"
        },
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
