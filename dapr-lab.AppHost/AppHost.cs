var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.SampleApp>("sampleapp");

builder.Build().Run();
