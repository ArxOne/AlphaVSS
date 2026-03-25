using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.VSWhere;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.Tools.NuGet.NuGetTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;
using Nuke.Common.CI.AzurePipelines;
using System.Threading;
using System.Text;

using Nuke.Common.CI.GitHubActions;
using Nuke.Common.Tools.DocFX;

using Serilog.Core;

using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.PathConstruction;

[UnsetVisualStudioEnvironmentVariables]
[MSBuildVerbosityMapping]
class AlphaVssBuild : NukeBuild
{
   public static int Main() => Execute<AlphaVssBuild>(x => x.Compile);


   [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
   readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

   [Parameter]
   readonly string FeedUri;

   [Parameter]
   readonly string NuGetApiKey = "VSTS";

   [Solution] readonly Solution Solution;
   [GitRepository] readonly GitRepository GitRepository;
   [GitVersion] readonly GitVersion GitVersion;

   string RequiredMSBuildVersion = "[18.0,)";

   AbsolutePath SourceDirectory => RootDirectory / "src";
   AbsolutePath ArtifactsDirectory => RootDirectory / "artifacts";
   AbsolutePath NuSpecDirectory => RootDirectory / "build" / "nuget";
   AbsolutePath PackageArtifactsDirectory => ArtifactsDirectory / "package";

   string MSBuildToolPath;

   protected override void OnBuildInitialized()
   {
      base.OnBuildInitialized();
      var result =
         VSWhereTasks.VSWhere(s => s
            .SetVersion(RequiredMSBuildVersion)
            .EnableLatest()
            .EnablePrerelease()
            .EnableUTF8()
            .SetProperty("InstallationPath")
            .SetFormat(VSWhereFormat.value)
      ).Output.LastOrDefault().Text;

      MSBuildToolPath = Path.Combine(result, "MSBuild\\Current\\Bin\\MSBuild.exe");
      if (IsServerBuild)
      {
         AzurePipelines.Instance.UpdateBuildNumber($"AlphaVSS-{GitVersion.SemVer}")
            ;
      }
   }

   Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
           SourceDirectory.GlobDirectories("**\\bin", "**\\obj").ForEach(f => f.DeleteDirectory());
           ArtifactsDirectory.CreateOrCleanDirectory();
        });

   Target Restore => _ => _
       .Executes(() =>
       {
          MSBuild(_ => _
             .SetProcessToolPath(MSBuildToolPath)
             .SetTargetPath(Solution.Projects.FirstOrDefault(p => p.Name == "AlphaVSS.Common"))
             .SetTargets("Restore"));
          MSBuild(_ => _
             .SetProcessToolPath(MSBuildToolPath)
             .SetTargetPath(Solution.Projects.FirstOrDefault(p => p.Name == "AlphaVSS.Platform"))
             .SetTargets("Restore"));
       });

   Target Compile => _ => _
       .DependsOn(Restore)
       .Executes(() =>
       {
          BuildProject("AlphaVSS.Common", Configuration == Configuration.Debug ? "net8d" : "net8", "AnyCPU");
          
          BuildPlatformProject("net8");


          void BuildPlatformProject(string projectConfigurationPrefix)
          {
             projectConfigurationPrefix = Configuration == Configuration.Debug ? $"{projectConfigurationPrefix}d" : projectConfigurationPrefix;
             BuildProject("AlphaVSS.Platform", projectConfigurationPrefix, "Win32");
             BuildProject("AlphaVSS.Platform", projectConfigurationPrefix, "x64");
             BuildProject("AlphaVSS.Platform", projectConfigurationPrefix, "arm64");
          }

          void BuildProject(string projectName, string configuration, string platform)
          {
             var project = Solution.AllProjects.FirstOrDefault(p => p.Name == projectName).NotNull($"Unable to find project named {projectName} in solution {Solution.Name}");

             MSBuild(s => s
                  .SetProcessToolPath(MSBuildToolPath)
                  .SetTargetPath(project)
                  .SetTargetPlatform((MSBuildTargetPlatform)platform)
                  .SetConfiguration(configuration)
                  .SetTargets("Build")
                  .SetAssemblyVersion(GitVersion.AssemblySemVer)
                  .SetFileVersion(GitVersion.AssemblySemFileVer)
                  .SetInformationalVersion(GitVersion.InformationalVersion)
                  .AddProperty("BuildProjectReferences", true)
                  .AddProperty("AlphaVss_VersionMajor", 1)
                  .SetInformationalVersion(GitVersion.InformationalVersion)
                  .SetMaxCpuCount(Environment.ProcessorCount)
                  .SetNodeReuse(IsLocalBuild));
          }
       });


   Target Build => _ => _
      .DependsOn(Clean, Compile);

   Target Pack => _ => _
      .DependsOn(Build)
      .Executes(() =>
      {
         var version = GitVersion.NuGetVersion;
         if (IsLocalBuild)
            version += DateTime.UtcNow.ToString("yyMMddHHmmss");

         foreach (var nuspec in NuSpecDirectory.GlobFiles("*.nuspec"))
         {
            NuGetPack(s => s
               .SetMSBuildPath(MSBuildToolPath)
               .SetOutputDirectory(ArtifactsDirectory)
               .AddProperty("branch", GitVersion.BranchName)
               .AddProperty("commit", GitVersion.Sha)
               .SetVersion(version)
               .SetTargetPath(nuspec)
            );
         }
      });

   Target Push => _ => _
      .DependsOn(Pack)
      .Requires(() => FeedUri)
      .Executes(() =>
      {
         foreach (var file in ArtifactsDirectory.GlobFiles("*.nupkg"))
         {
            NuGetPush(s => s
               .SetApiKey(NuGetApiKey)
               .SetSource(FeedUri)
               .SetTargetPath(file));
         }
      });

   Target UploadArtifacts => _ => _
      .DependsOn(Clean, Pack)
      .OnlyWhenStatic(() => IsServerBuild)
      .Executes(() =>
      {
         foreach (var file in ArtifactsDirectory.GlobFiles("*.nupkg"))
         {
            UploadAzureArtifact("Package", "Package", file);
         }
         //UploadAzureArtifact("Package", "Package", null);

         Thread.Sleep(2000);
      });

   private void UploadAzureArtifact(string containerFolder, string artifactName, string fileName)
   {
      // ##vso[artifact.upload containerfolder=testresult;artifactname=uploadedresult;]c:\testresult.trx
      StringBuilder command = new StringBuilder("##vso[artifact.upload containerfolder=");
      command.Append(containerFolder);
      command.Append(';');
      if (!String.IsNullOrEmpty(artifactName))
      {
         command.Append("artifactname=");
         command.Append(artifactName);
         command.Append(';');
      }

      command.Append("]");
      if (!String.IsNullOrEmpty(fileName))
         command.Append(fileName);

      Console.WriteLine(command.ToString());
   }

   Target DistBuild => _ => _
      .DependsOn(Pack, UploadArtifacts);

}
