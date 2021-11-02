using System.Xml;
using System.Xml.Schema;
using BuildToolUtilities;

namespace BuildTool
{
	// Implementation of XmlDocument which preserves line numbers for its elements
	class XMLConfigFile : XmlDocument
	{
		public static readonly string RootElementName    = "Configuration"; // Root element for the XML document
		public static readonly string SchemaNamespaceURI = "https://www.MyEngine.com/BuildConfiguration";
		readonly FileReference        File;       // The file being read
		IXmlLineInfo                  LineInfo;   // Interface to the LineInfo on the active XmlReader
		bool                          bHasErrors; // Set to true if the reader encounters an error

		// Private constructor. Use XmlConfigFile.TryRead to read an XML config file.
		private XMLConfigFile(FileReference InFile)
		{
			File = InFile;
		}

		// Overrides XmlDocument.CreateElement() to construct ScriptElements rather than XmlElements
		public override XmlElement CreateElement(string Prefix, string LocalName, string NamespaceUri)
		{
			return new XMLConfigFileElement(File, LineInfo.LineNumber, Prefix, LocalName, NamespaceUri, this);
		}

		// Loads a script document from the given file
		public static bool TryRead(FileReference FileToLoad, XmlSchema SchemaToValidate, out XMLConfigFile OutConfigFile)
		{
			XMLConfigFile ConfigFile = new XMLConfigFile(FileToLoad);

			XmlReaderSettings Settings = new XmlReaderSettings
			{
				ValidationType = ValidationType.Schema
			};
			Settings.ValidationEventHandler += ConfigFile.ValidationEvent;
			Settings.Schemas.Add(SchemaToValidate);

			using (XmlReader Reader = XmlReader.Create(FileToLoad.FullName, Settings))
			{
				// Read the document
				ConfigFile.LineInfo = (IXmlLineInfo)Reader;
				try
				{
					ConfigFile.Load(Reader);
				}
				catch (XmlException Ex)
				{
					if (!ConfigFile.bHasErrors)
					{
						Log.TraceError(FileToLoad, Ex.LineNumber, "{0}", Ex.Message);
						ConfigFile.bHasErrors = true;
					}
				}

				// If we hit any errors while parsing
				if (ConfigFile.bHasErrors)
				{
					OutConfigFile = null;
					return false;
				}

				// Check that the root element is valid. If not, we didn't actually validate against the schema.
				if (ConfigFile.DocumentElement.Name != RootElementName)
				{
					Log.TraceError("Script does not have a root element called '{0}'", RootElementName);
					OutConfigFile = null;
					return false;
				}
				if (ConfigFile.DocumentElement.NamespaceURI != SchemaNamespaceURI)
				{
					Log.TraceError("Script root element is not in the '{0}' namespace (add the xmlns=\"{0}\" attribute)", SchemaNamespaceURI);
					OutConfigFile = null;
					return false;
				}
			}

			OutConfigFile = ConfigFile;
			return true;
		}

		// Callback for validation errors in the document
		void ValidationEvent(object Sender, ValidationEventArgs Args)
		{
			Log.TraceWarning(File, Args.Exception.LineNumber, "{0}", Args.Message);
		}
	}

	// Implementation of XmlElement which preserves line numbers
	class XMLConfigFileElement : XmlElement
	{
		public readonly FileReference File;       // The file containing this element
		public readonly int           LineNumber; // The line number containing this element

		public XMLConfigFileElement(FileReference InFile, int InLineNumber, string Prefix, string LocalName, string NamespaceUri, XMLConfigFile ConfigFile)
			: base(Prefix, LocalName, NamespaceUri, ConfigFile)
		{
			File       = InFile;
			LineNumber = InLineNumber;
		}
	}
}
