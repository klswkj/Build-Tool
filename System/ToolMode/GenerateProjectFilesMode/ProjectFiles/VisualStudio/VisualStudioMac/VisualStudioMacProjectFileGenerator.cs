// Copyright Epic Games, Inc. All Rights Reserved.

using System;
using System.Collections.Generic;
using System.Text;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BuildToolUtilities;

namespace BuildTool
{
    
    
    // Visual Studio for Mac project file generator implementation
    
    class VCMacProjectFileGenerator : VCProjectFileGenerator
    {
       
        
        // Default constructor
        
        // <param name="InOnlyGameProject">The single project to generate project files for, or null</param>
        // <param name="InArguments">Additional command line arguments</param>
        public VCMacProjectFileGenerator(FileReference InOnlyGameProject, CommandLineArguments InArguments)
			: base(InOnlyGameProject, VCProjectFileFormat.Default, InArguments)
        {
            // no suo file, requires ole32
            bWriteSolutionOptionFile = false;
        }

        // True if we should include IntelliSense data in the generated project files when possible
        override public bool GetbGenerateIntelliSenseData()
        {
            return false;
        }

        
        // Writes the project files to disk
        
        // <returns>True if successful</returns>
        protected override bool WriteProjectFiles(PlatformProjectGeneratorCollection PlatformProjectGenerators)
        {
            // This can be reset by higher level code when it detects that we don't have
            // VS2015 installed (TODO - add custom format for Mac?)
            ProjectFileFormat = VCProjectFileFormat.VisualStudio2015;

            // we can't generate native projects so clear them here, we will just
            // write out OtherProjectFiles and AutomationProjectFiles
            GeneratedProjectFiles.Clear();

            if (!base.WriteProjectFiles(PlatformProjectGenerators))
            {
                return false;
            }


            // Write AutomationReferences file
            if (AutomationProjectFiles.Any())
            {
                XNamespace NS = XNamespace.Get("http://schemas.microsoft.com/developer/msbuild/2003");

                DirectoryReference AutomationToolDir = DirectoryReference.Combine(BuildTool.EngineSourceDirectory, "Programs", "AutomationTool");
                new XDocument(
                    new XElement(NS + "Project",
                        new XAttribute("ToolsVersion", VCProjectFileGenerator.GetMSBuildToolsVersionString(ProjectFileFormat)),
                        new XAttribute("DefaultTargets", "Build"),
                        new XElement(NS + "ItemGroup",
                            from AutomationProject in AutomationProjectFiles
                            select new XElement(NS + "ProjectReference",
                                new XAttribute("Include", AutomationProject.ProjectFilePath.MakeRelativeTo(AutomationToolDir)),
                                new XElement(NS + "Project", (AutomationProject as VCSharpProjectFile).ProjectGUID.ToString("B")),
                                new XElement(NS + "Name", AutomationProject.ProjectFilePath.GetFileNameWithoutExtension()),
                                new XElement(NS + "Private", "false")
                            )
                        )
                    )
                ).Save(FileReference.Combine(AutomationToolDir, "AutomationTool.csproj.References").FullName);
            }

            return true;
        }

    }

}
