using System;
using System.IO;
using System.Linq;
using Nuke.Common;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.MSBuild;
using Nuke.Common.Tools.VSWhere;
using Nuke.Common.Tools.NuGet;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.Tools.NuGet.NuGetTasks;
using static Nuke.Common.Tools.MSBuild.MSBuildTasks;

using Serilog;

[UnsetVisualStudioEnvironmentVariables]
[MSBuildVerbosityMapping]
class AlphaVssBuild : NukeBuild
{
   public static int Main() => Execute<AlphaVssBuild>(x => x.Compile);


   [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
   readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

   [Parameter][Secret] readonly string NuGetApiKey;

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

      MSBuildToolPath = Path.Combine(result ?? @"C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools", "MSBuild\\Current\\Bin\\MSBuild.exe");
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
      .Executes(() =>
      {
         var feedUri = Environment.GetEnvironmentVariable("FeedUri");
         Log.Information($"Feed Uri: {feedUri}");
         foreach (var file in ArtifactsDirectory.GlobFiles("*.nupkg"))
         {
            NuGetPush(s => s
               .SetApiKey(NuGetApiKey)
               .SetSource(feedUri)
               .SetTargetPath(file));
         }
      });

}
