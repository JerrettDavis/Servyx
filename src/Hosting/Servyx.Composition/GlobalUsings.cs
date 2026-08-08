// The 20 files moved into this project from Servyx.Web/Services verbatim (namespace line only changed) relied
// on Microsoft.NET.Sdk.Web's implicit global usings for these four namespaces — Servyx.Web.csproj's
// <ImplicitUsings>enable</ImplicitUsings> combined with the Sdk.Web project type generates them automatically
// (see Servyx.Web's own obj/**/Servyx.Web.GlobalUsings.g.cs). This project's SDK is the plain
// Microsoft.NET.Sdk, whose implicit usings cover only the common System.* set, so these four are restated here
// rather than editing every moved file's own using list.
global using Microsoft.Extensions.Configuration;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
