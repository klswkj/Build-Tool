using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using BuildToolUtilities;

namespace BuildTool
{
	internal static class RulesDocumentationTag
    {
    }

	internal static class RulesDocumentation
	{
		public static void WriteDocumentation(Type RulesType, FileReference OutputFile)
		{
			// Get the path to the XML documentation
			FileReference InputDocumentationFile = new FileReference(Assembly.GetExecutingAssembly().Location).ChangeExtension(".xml");

			if(!FileReference.Exists(InputDocumentationFile))
			{
				throw new BuildException("Generated assembly documentation not found at {0}.", InputDocumentationFile);
			}

			// Read the documentation
			XmlDocument InputDocumentation = new XmlDocument();
			InputDocumentation.Load(InputDocumentationFile.FullName);

			// Filter the properties into read-only and read/write lists
			List<FieldInfo> ReadOnlyFields  = new List<FieldInfo>();
			List<FieldInfo> ReadWriteFields = new List<FieldInfo>();

			foreach(FieldInfo Field in RulesType.GetFields(BindingFlags.Instance | BindingFlags.SetProperty | BindingFlags.Public))
			{
				if(!Field.FieldType.IsClass || !Field.FieldType.Name.EndsWith(Tag.Module.TargetRulesSuffix))
				{
					if(Field.IsInitOnly)
					{
						ReadOnlyFields.Add(Field);
					}
					else
					{
						ReadWriteFields.Add(Field);
					}
				}
			}

			// Make sure the output file is writable
			if(FileReference.Exists(OutputFile))
			{
				FileReference.MakeWriteable(OutputFile);
			}
			else
			{
				DirectoryReference.CreateDirectory(OutputFile.Directory);
			}

			// Generate the documentation file
			if (OutputFile.HasExtension(Tag.Ext.Document))
			{
				WriteDocumentation(OutputFile, ReadOnlyFields, ReadWriteFields, InputDocumentation);
			}
			else if (OutputFile.HasExtension(Tag.Ext.Html))
			{
				WriteDocumentationHTML(OutputFile, ReadOnlyFields, ReadWriteFields, InputDocumentation);
			}
			else
			{
				throw new BuildException("Unable to detect format from extension of output file ({0})", OutputFile);
			}

			// Success!
			Log.TraceInformation("Written documentation to {0}.", OutputFile);
		}

        private static void WriteDocumentation(FileReference OutputFile, List<FieldInfo> ReadOnlyFields, List<FieldInfo> ReadWriteFields, XmlDocument InputDocumentation)
		{
			// Generate the UDN documentation file
			using (StreamWriter Writer = new StreamWriter(OutputFile.FullName))
			{
				Writer.WriteLine("Availability: NoPublish");
				Writer.WriteLine("Title: Build Configuration Properties Page");
				Writer.WriteLine("Crumbs:");
				Writer.WriteLine("Description: This is a procedurally generated markdown page.");
				Writer.WriteLine("Version: {0}.{1}", ReadOnlyBuildVersion.Current.MajorVersion, ReadOnlyBuildVersion.Current.MinorVersion);
				Writer.WriteLine("");
				if (0 < ReadOnlyFields.Count)
				{
					Writer.WriteLine("### Read-Only Properties");
					Writer.WriteLine();
					foreach (FieldInfo Field in ReadOnlyFields)
					{
						WriteDocumentationField(InputDocumentation, Field, Writer);
					}
					Writer.WriteLine();
				}
				if (0 < ReadWriteFields.Count)
				{
					Writer.WriteLine("### Read/Write Properties");
					foreach (FieldInfo Field in ReadWriteFields)
					{
						WriteDocumentationField(InputDocumentation, Field, Writer);
					}
					Writer.WriteLine("");
				}
			}
		}

        private static void WriteDocumentationField(XmlDocument InputDocumentation, FieldInfo Field, TextWriter Writer)
		{
			XmlNode Node = InputDocumentation.SelectSingleNode(String.Format("//member[@name='F:{0}.{1}']/summary", Field.DeclaringType.FullName, Field.Name));
			if (Node != null)
			{
				// Reflow the comments into paragraphs, assuming that each paragraph will be separated by a blank line
				List<string> Lines = new List<string>(Node.InnerText.Trim().Split('\n').Select(x => x.Trim()));
				for (int Idx = Lines.Count - 1; 0 < Idx; --Idx)
				{
					if (0 < Lines[Idx - 1].Length && !Lines[Idx].StartsWith("*") && !Lines[Idx].StartsWith("-"))
					{
						Lines[Idx - 1] += " " + Lines[Idx];
						Lines.RemoveAt(Idx);
					}
				}

				// Write the values of the enum
				/*				if(Field.FieldType.IsEnum)
								{
									Lines.Add("Valid values are:");
									foreach(string Value in Enum.GetNames(Field.FieldType))
									{
										Lines.Add(String.Format("* {0}.{1}", Field.FieldType.Name, Value));
									}
								}
				*/
				// Write the result to the .udn file
				if (0 < Lines.Count)
				{
					Writer.WriteLine("$ {0} ({1}): {2}", Field.Name, GetPrettyTypeName(Field.FieldType), Lines[0]);
					for (int Idx = 1; Idx < Lines.Count; ++Idx)
					{
						if (Lines[Idx].StartsWith("*") || Lines[Idx].StartsWith("-"))
						{
							Writer.WriteLine("        * {0}", Lines[Idx].Substring(1).TrimStart());
						}
						else
						{
							Writer.WriteLine("    * {0}", Lines[Idx]);
						}
					}
					Writer.WriteLine();
				}
			}
		}

		static void WriteDocumentationHTML(FileReference OutputFile, List<FieldInfo> ReadOnlyFields, List<FieldInfo> ReadWriteFields, XmlDocument InputDocumentation)
		{
			using (StreamWriter Writer = new StreamWriter(OutputFile.FullName))
			{
				Writer.WriteLine("<html>");
				Writer.WriteLine("  <body>");
				if (0 < ReadOnlyFields.Count)
				{
					Writer.WriteLine("    <h2>Read-Only Properties</h2>");
					Writer.WriteLine("    <dl>");
					foreach (FieldInfo Field in ReadOnlyFields)
					{
						WriteFieldHTML(InputDocumentation, Field, Writer);
					}
					Writer.WriteLine("    </dl>");
				}
				if (0 < ReadWriteFields.Count)
				{
					Writer.WriteLine("    <h2>Read/Write Properties</h2>");
					Writer.WriteLine("    <dl>");
					foreach (FieldInfo Field in ReadWriteFields)
					{
						WriteFieldHTML(InputDocumentation, Field, Writer);
					}
					Writer.WriteLine("    </dl>");
				}
				Writer.WriteLine("  </body>");
				Writer.WriteLine("</html>");
			}
		}

		static void WriteFieldHTML(XmlDocument InputDocumentation, FieldInfo Field, TextWriter Writer)
		{
			XmlNode Node = InputDocumentation.SelectSingleNode(String.Format("//member[@name='F:{0}.{1}']/summary", Field.DeclaringType.FullName, Field.Name));
			if(Node != null)
			{
				// Reflow the comments into paragraphs, assuming that each paragraph will be separated by a blank line
				List<string> Lines = new List<string>(Node.InnerText.Trim().Split('\n').Select(x => x.Trim()));
				for(int Idx = Lines.Count - 1; 0 < Idx; --Idx)
				{
					if(0 < Lines[Idx - 1].Length && !Lines[Idx].StartsWith("*") && !Lines[Idx].StartsWith("-"))
					{
						Lines[Idx - 1] += " " + Lines[Idx];
						Lines.RemoveAt(Idx);
					}
				}

#if DEBUG
                System.Diagnostics.Debugger.Break();
				// Write the values of the enum
				// 				if(Field.FieldType.IsEnum)
				//				{
				//					Lines.Add("Valid values are:");
				//					foreach(string Value in Enum.GetNames(Field.FieldType))
				//					{
				//						Lines.Add(String.Format("* {0}.{1}", Field.FieldType.Name, Value));
				//					}
				//				}
#endif
				// Write the result to the HTML file
				if (0 < Lines.Count)
				{
					Writer.WriteLine("      <dt>{0} ({1})</dt>", Field.Name, GetPrettyTypeName(Field.FieldType));

					if(Lines.Count == 1)
					{
						Writer.WriteLine("      <dd>{0}</dd>", Lines[0]);
					}
					else
					{
						Writer.WriteLine("      <dd>");
						for(int Idx = 0; Idx < Lines.Count; Idx++)
						{
							if(Lines[Idx].StartsWith("*") || Lines[Idx].StartsWith("-"))
							{
								Writer.WriteLine("        <ul>");
								for(; Idx < Lines.Count && (Lines[Idx].StartsWith("*") || Lines[Idx].StartsWith("-")); Idx++)
								{
									Writer.WriteLine("          <li>{0}</li>", Lines[Idx].Substring(1).TrimStart());
								}
								Writer.WriteLine("        </ul>");
							}
							else
							{
								Writer.WriteLine("        {0}", Lines[Idx]);
							}
						}
						Writer.WriteLine("      </dd>");
					}
				}
			}
		}

		static string GetPrettyTypeName(Type FieldType)
		{
			if(FieldType.IsGenericType)
			{
				return String.Format("{0}&lt;{1}&gt;", FieldType.Name.Substring(0, FieldType.Name.IndexOf('`')), String.Join(", ", FieldType.GenericTypeArguments.Select(x => GetPrettyTypeName(x))));
			}
			else
			{
				return FieldType.Name;
			}
		}
	}
}
