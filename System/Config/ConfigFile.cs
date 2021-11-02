using System;
using System.Collections.Generic;
using System.IO;
using BuildToolUtilities;

namespace BuildTool
{
	// Specifies the action to take for a config line, as denoted by its prefix.
	public enum ConfigLineAction
	{
		Set,           // Assign the value to the key
		Add,           // Add the value to the key (denoted with +X=Y in config files)
		RemoveKey,     // Remove the key without having to match value (denoted with !X in config files)
		RemoveKeyValue // Remove the matching key and value (denoted with -X=Y in config files)
	}

	// Contains a pre-parsed raw config line, consisting of action, key and value components.
	public class ConfigLine
	{
		public ConfigLineAction ActionToTakeWhenMerging; // The action to take when merging this key/value pair with an existing value
		public string Key; // Name of the key to modify
		public string Value; // Value to assign to the key

		public ConfigLine(ConfigLineAction InActionToTakeWhenMerging, string KeyToModify, string ValueToAssign)
		{
			ActionToTakeWhenMerging = InActionToTakeWhenMerging;
			Key   = KeyToModify;
			Value = ValueToAssign;
		}

		public override string ToString()
		{
			string Prefix = (ActionToTakeWhenMerging == ConfigLineAction.Add)? "+" : 
				(ActionToTakeWhenMerging == ConfigLineAction.RemoveKey)? "!" : 
				(ActionToTakeWhenMerging == ConfigLineAction.RemoveKeyValue) ? "-" : "";
			return String.Format("{0}{1}={2}", Prefix, Key, Value);
		}
	}

	// Contains the lines which appeared in a config section, with all comments and whitespace removed
	public class ConfigFileSection
	{
		public string SectionName; // Name of the section
		public List<ConfigLine> ConfigLines = new List<ConfigLine>(); // Lines which appeared in the config section

		public ConfigFileSection(string ConfigSectionName)
		{
			SectionName = ConfigSectionName;
		}

		public bool TryGetLine(string Name, out ConfigLine OutLine)
		{
			foreach ( ConfigLine Line in ConfigLines)
			{
				if (Line.Key.Equals(Name))
				{
					OutLine = Line;
					return true;
				}
			}
			OutLine = null;
			return false;
		}
	}
	
	// Represents a single config file as stored on disk. 
	public class ConfigFile
	{
		// Maps names to config sections
		private readonly Dictionary<string, ConfigFileSection> Sections 
			= new Dictionary<string, ConfigFileSection>(StringComparer.InvariantCultureIgnoreCase);

		// Reads config data from the given file.
		// <param name="Location">File to read from</param>
		// <param name="DefaultAction">The default action to take when encountering arrays without a '+' prefix</param>
		public ConfigFile(FileReference FileToRead, ConfigLineAction DefaultAction = ConfigLineAction.Set)
		{
			using (StreamReader Reader = new StreamReader(FileToRead.FullName))
			{
				ConfigFileSection CurrentSection = null;
				while(!Reader.EndOfStream)
				{
					// Find the first non-whitespace character
					string Line = Reader.ReadLine();
					for(int StartIdx = 0; StartIdx < Line.Length; ++StartIdx)
					{
						if (Line[StartIdx] != ' ' && Line[StartIdx] != '\t')
						{
							// Find the last non-whitespace character. If it's an escaped newline, merge the following line with it.
							int EndIdx = Line.Length;
							while (EndIdx > StartIdx)
							{
								if(Line[EndIdx - 1] == '\\')
								{
									string NextLine = Reader.ReadLine();
									if(NextLine == null)
									{
										break;
									}
									Line += NextLine;
									EndIdx = Line.Length;
									continue;
								}
								if(Line[EndIdx - 1] != ' ' && Line[EndIdx - 1] != '\t')
								{
									break;
								}

								EndIdx--;
							}

							// Break out if we've got a comment
							if(Line[StartIdx] == ';')
							{
								break;
							}
							if(Line[StartIdx] == '/' &&
								StartIdx + 1 < Line.Length && 
								Line[StartIdx + 1] == '/')
							{
								break;
							}

							// Check if it's the start of a new section
							if(Line[StartIdx] == '[')
							{
								CurrentSection = null;
								if(Line[EndIdx - 1] == ']')
								{
									string SectionName = Line.Substring(StartIdx + 1, EndIdx - StartIdx - 2);
									if(!Sections.TryGetValue(SectionName, out CurrentSection))
									{
										CurrentSection = new ConfigFileSection(SectionName);
										Sections.Add(SectionName, CurrentSection);
									}
								}
								break;
							}

							// Otherwise add it to the current section or add a new one
							if(CurrentSection != null)
							{
								if(!TryAddConfigLine(CurrentSection, Line, StartIdx, EndIdx, DefaultAction))
								{
									Log.TraceWarning("Couldn't parse '{0}' in {1} of {2}", Line, CurrentSection, FileToRead.FullName);
								}
								break;
							}

							// Otherwise just ignore it
							break;
						} // End If if (Line[StartIdx] != ' ' && Line[StartIdx] != '\t')
					} // End for loop
				} // End while(!Reader.EndOfStream)
			} // End using StreamReader
		}

		// Reads config data from the given string.
		// <param name="IniText">Single line string of config settings in the format [Section1]:Key1=Value1,[Section2]:Key2=Value2</param>
		// <param name="DefaultAction">The default action to take when encountering arrays without a '+' prefix</param>
		public ConfigFile(string IniText, ConfigLineAction DefaultAction = ConfigLineAction.Set)
		{
			// Break into individual settings of the form [Section]:Key=Value
			string[] SettingLines = IniText.Split(new char[] { ',' });
			foreach (string Setting in SettingLines)
			{
				// Locate and break off the section name
				string SectionName = Setting.Remove(Setting.IndexOf(':')).Trim(new char[] { '[', ']' });
				if (0 < SectionName.Length)
				{
					if (!Sections.TryGetValue(SectionName, out ConfigFileSection CurrentSection))
					{
						CurrentSection = new ConfigFileSection(SectionName);
						Sections.Add(SectionName, CurrentSection);
					}

					if (CurrentSection != null)
					{
						string IniKeyValue = Setting.Substring(Setting.IndexOf(':') + 1);
						if (!TryAddConfigLine(CurrentSection, IniKeyValue, 0, IniKeyValue.Length, DefaultAction))
						{
							Log.TraceWarning("Couldn't parse '{0}'", IniKeyValue);
						}
					}
				}
			}
		}

		
		// Try to parse a key/value pair from the given line, and add it to a config section
		// <param name="Section">The section to receive the parsed config line</param>
		// <param name="Line">Text to parse</param>
		// <param name="StartIdx">Index of the first non-whitespace character in this line</param>
		// <param name="EndIdx">Index of the last (exclusive) non-whitespace character in this line</param>
		// <param name="DefaultAction">The default action to take if '+' or '-' is not specified on a config line</param>
		// <returns>True if the line was parsed correctly, false otherwise</returns>
		static bool TryAddConfigLine(ConfigFileSection Section, string Line, int StartIdx, int EndIdx, ConfigLineAction DefaultAction)
		{
			// Find the '=' character separating key and value
			int EqualsIdx = Line.IndexOf('=', StartIdx, EndIdx - StartIdx);
			if(EqualsIdx == -1 && Line[StartIdx] != '!')
			{
				return false;
			}

			int KeyStartIdx = StartIdx; // Keep track of the start of the key name
			ConfigLineAction Action = DefaultAction; // Remove the +/-/! prefix, if present

			if (Line[KeyStartIdx] == '+' || 
				Line[KeyStartIdx] == '-' || 
				Line[KeyStartIdx] == '!')
			{
				Action = (Line[KeyStartIdx] == '+')? 
					ConfigLineAction.Add : 
					(Line[KeyStartIdx] == '!') ? ConfigLineAction.RemoveKey : 
					ConfigLineAction.RemoveKeyValue;

				++KeyStartIdx;

				while(Line[KeyStartIdx] == ' ' ||
					  Line[KeyStartIdx] == '\t')
				{
					++KeyStartIdx;
				}
			}

            // RemoveKey actions do not require a value
            if (Action == ConfigLineAction.RemoveKey && EqualsIdx == -1)
            {
                Section.ConfigLines.Add(new ConfigLine(Action, Line.Substring(KeyStartIdx).Trim(), ""));
                return true;
            }

            // Remove trailing spaces after the name of the key
            int KeyEndIdx = EqualsIdx;
			for(; KeyStartIdx < KeyEndIdx; --KeyEndIdx)
			{
				if(Line[KeyEndIdx - 1] != ' ' && 
				   Line[KeyEndIdx - 1] != '\t')
				{
					break;
				}
			}

			// Make sure there's a non-empty key name
			if(KeyStartIdx == EqualsIdx)
			{
				return false;
			}

			// Skip whitespace between the '=' and the start of the value
			int ValueStartIdx = EqualsIdx + 1;

			for(; ValueStartIdx < EndIdx; ++ValueStartIdx)
			{
				if(Line[ValueStartIdx] != ' ' && Line[ValueStartIdx] != '\t')
				{
					break;
				}
			}

			// Strip quotes around the value if present
			int ValueEndIdx = EndIdx;

			if(ValueStartIdx + 2 <= ValueEndIdx && 
				Line[ValueStartIdx] == '"' && 
				Line[ValueEndIdx - 1] == '"')
			{
				++ValueStartIdx;
				--ValueEndIdx;
			}

			// Add it to the config section
			string Key   = Line.Substring(KeyStartIdx, KeyEndIdx - KeyStartIdx);
			string Value = Line.Substring(ValueStartIdx, ValueEndIdx - ValueStartIdx);
			Section.ConfigLines.Add(new ConfigLine(Action, Key, Value));
			return true;
		}

		// Names of sections in this file
		public IEnumerable<string> SectionNames => Sections.Keys;

		// Tries to get a config section by name, or creates one if it doesn't exist
		// <param name="SectionName">Name of the section to look for</param>
		// <returns>The config section</returns>
		public ConfigFileSection FindOrAddSection(string SectionName)
		{
			if (!Sections.TryGetValue(SectionName, out ConfigFileSection Section))
			{
				Section = new ConfigFileSection(SectionName);
				Sections.Add(SectionName, Section);
			}
			return Section;
		}

		// Tries to get a config section by name
		public bool TryGetSection(string SectionName, out ConfigFileSection RawSection)
		{
			return Sections.TryGetValue(SectionName, out RawSection);
		}

		// Write the config file out to the given location. Useful for debugging.
		public void Write(FileReference FileToWrite)
		{
			using (StreamWriter Writer = new StreamWriter(FileToWrite.FullName))
			{
				foreach (ConfigFileSection Section in Sections.Values)
				{
					Writer.WriteLine("[{0}]", Section.SectionName);
					foreach (ConfigLine Line in Section.ConfigLines)
					{
						Writer.WriteLine("{0}", Line.ToString());
					}
					Writer.WriteLine();
				}
			}
		}
	}
}
