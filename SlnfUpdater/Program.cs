﻿using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Locator;
using Pastel;
using SlnfUpdater.Helper;
using System.Drawing;
using System.Runtime;

namespace SlnfUpdater
{
    internal class Program
    {
        private static System.Drawing.Color SolutionProjectColor = Color.FromArgb(165, 229, 250);
        private static System.Drawing.Color TimeColor = Color.FromArgb(220, 110, 0);
        private static System.Drawing.Color NoReferenceColor = Color.FromArgb(255, 255, 0);
        private static System.Drawing.Color NewReferenceColor = Color.FromArgb(0, 255, 0);

        static void Main(string[] args)
        {
            var before = DateTime.Now;

            MSBuildLocator.RegisterDefaults();

            var slnfFolderPath = Path.GetFullPath(args[0]);
            var slnfFileMask = args[1];

            if (!Directory.Exists(slnfFolderPath))
            {
                Console.WriteLine($"Folder {slnfFolderPath} does not exist.");
                return;
            }

            Console.WriteLine($"Found folder {slnfFolderPath}");

            var slnfFiles = Directory.GetFiles(slnfFolderPath, slnfFileMask, SearchOption.TopDirectoryOnly)
                .Select(f => new FileInfo(f).Name)
                .ToList();
            if (slnfFiles.Count == 0)
            {
                Console.WriteLine($"Folder {slnfFolderPath} does not contains a slnf files.");
                return;
            }

            Console.WriteLine($"Found {slnfFiles.Count} slnf files:");

            foreach (var slnfFile in slnfFiles)
            {
                Console.WriteLine($"    {slnfFile.Pastel(SolutionProjectColor)}");
            }

            var processedCount = 0;
            //foreach (var slnfFile in slnfFiles)
            Parallel.ForEach(slnfFiles, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 1) }, slnfFileName =>
            {
                var slnfFilePath = Path.Combine(slnfFolderPath, slnfFileName);

                Console.WriteLine($"Processing {slnfFilePath.Pastel(SolutionProjectColor)}...");

                var result = ProcessSlnfFile(
                    slnfFilePath
                    );

                Interlocked.Increment(ref processedCount);

                Console.WriteLine($"""
({processedCount}/{slnfFiles.Count}) Finished {slnfFilePath.Pastel(SolutionProjectColor)}.
{result}
""");

            }
            );

            var after = DateTime.Now;
            Console.WriteLine($"Finished (taken {(after - before).ToString().Pastel(TimeColor)}).");
        }

        private static string ProcessSlnfFile(
            string slnfFilePath
            )
        {
            var slnFile = Microsoft.Build.Construction.SolutionFile.Parse(
                slnfFilePath
                );

            //TODO: https://github.com/dotnet/msbuild/issues/9981
            var slnfProjectsRelativeSlnField = slnFile.GetType().GetField("_solutionFilter", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var slnfProjectsRelativeSln = (IReadOnlySet<string>)slnfProjectsRelativeSlnField.GetValue(slnFile);

            var slnFullPathProperty = slnFile.GetType().GetProperty("FullPath", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var slnFullPath = (string)slnFullPathProperty.GetValue(slnFile);

            var context = new SearchReferenceContext(
                slnFullPath,
                slnfFilePath,
                slnFile,
                slnfProjectsRelativeSln
                );

            //process any found projects for its reference, which must be added to slnf later
            foreach (var slnfProjectRelativeSln in slnfProjectsRelativeSln)
            {
                var projectFilePath = Path.GetFullPath(
                    Path.Combine(
                        context.SlnfFolderPath,
                        slnfProjectRelativeSln
                        )
                    );
                var projectFileInfo = new FileInfo(projectFilePath);
                var projectFolderPath = projectFileInfo.Directory.FullName;

                ProcessProjectFromSlnf(
                    context,
                    projectFileInfo
                    );
            }

            if (context.AddedReferences.Count <= 0)
            {
                return $"   No new references found.".Pastel(NoReferenceColor);
            }

            var slnfBody = File.ReadAllLines(context.SlnfFilePath).ToList();
            var si = slnfBody.FindIndex(p => p.EndsWith("["));
            var ei = slnfBody.FindIndex(p => p.EndsWith("]"));

            slnfBody.RemoveRange(si + 1, ei - si - 1);

            var addedReferencesWithSort = context.GetSortedProjects();
            for (var i = 0; i < addedReferencesWithSort.Count; i++)
            {
                var addedReferenceWithSort = addedReferencesWithSort[i]
                    .Replace("\\", "\\\\");
                var last = i == 0; //the last has zero index because of descending sorting!

                if (last)
                {
                    slnfBody.Insert(si + 1, $"      \"{addedReferenceWithSort}\"");
                }
                else
                {
                    slnfBody.Insert(si + 1, $"      \"{addedReferenceWithSort}\",");
                }
            }

            File.WriteAllText(
                context.SlnfFilePath,
                string.Join(
                    Environment.NewLine,
                    slnfBody
                    )
                );

            var newReferences = string.Join(
                Environment.NewLine,
                context.AddedReferences.Select(r => "      " + r)
                ).Pastel(NewReferenceColor);

            return $"""
   New references:
{newReferences}
""";
        }

        private static void ProcessProjectFromSlnf(
            SearchReferenceContext context,
            FileInfo projectFileInfo
            )
        {
            if (projectFileInfo.Extension.In(
                ".shproj",
                ".vcxproj"
                ))
            {
                //skip this type of project
                return;
            }

            var projectFilePath = projectFileInfo.FullName;
            var projectFolderPath = projectFileInfo.Directory.FullName;

            List<ProjectItem> projectReferences = ProvideProjectReferences(
                projectFilePath
                );

            foreach (var projectReference in projectReferences)
            {
                var referenceProjectPathRelativeCurrentProject = projectReference.EvaluatedInclude;

                var referenceProjectFullPath = Path.GetFullPath(
                    Path.Combine(
                        projectFolderPath,
                        referenceProjectPathRelativeCurrentProject
                        )
                    );

                if (context.IsProcessed(referenceProjectFullPath))
                {
                    continue;
                }

                context.AddReferenceFullPathIfNew(referenceProjectFullPath);

                //process recursively
                ProcessProjectFromSlnf(
                    context,
                    new FileInfo(referenceProjectFullPath)
                    );
            }
        }

        private static List<ProjectItem> ProvideProjectReferences(
            string projectFilePath
            )
        {
            var projectXmlRoot = Microsoft.Build.Construction.ProjectRootElement.Open(
                projectFilePath
                );

            using (var projectCollection = new Microsoft.Build.Evaluation.ProjectCollection())
            {
                Microsoft.Build.Evaluation.Project? evaluatedProject = null;
                try
                {
                    evaluatedProject = Microsoft.Build.Evaluation.Project.FromProjectRootElement(
                        projectXmlRoot,
                        new Microsoft.Build.Definition.ProjectOptions
                        {
                            ProjectCollection = projectCollection
                        }
                        );

                    return evaluatedProject.AllEvaluatedItems
                        .Where(p => p.ItemType == "ProjectReference")
                        .ToList();
                }
                finally
                {
                    if (evaluatedProject is not null)
                    {
                        projectCollection.UnloadProject(evaluatedProject);
                    }
                }
            }
        }
    }
}
