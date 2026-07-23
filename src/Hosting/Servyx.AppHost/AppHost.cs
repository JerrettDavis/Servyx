var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.Servyx_Web>("servyx-web");

builder.Build().Run();
