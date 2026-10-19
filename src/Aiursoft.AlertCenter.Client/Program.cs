using Aiursoft.AlertCenter.Client;
using Aiursoft.CommandFramework;

return await new NestedCommandApp()
    .WithFeature(new CiHandler())
    .WithFeature(new MetadataHandler())
    .WithFeature(new ListHandler())
    .WithFeature(new GetHandler())
    .WithFeature(new CreateHandler())
    .WithFeature(new MitigateHandler())
    .WithFeature(new ResolveHandler())
    .WithFeature(new SeverityHandler())
    .RunAsync(args);
