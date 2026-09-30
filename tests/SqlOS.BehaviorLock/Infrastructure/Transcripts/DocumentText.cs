using System.Text;
using System.Xml;

namespace SqlOS.BehaviorLock.Infrastructure.Transcripts;

/// <summary>Normalizes decoded documents for the transcript: XML is re-indented, other text keeps its lines.</summary>
public static class DocumentText
{
    public static string Normalize(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith('<'))
        {
            return trimmed.ReplaceLineEndings("\n");
        }

        var document = new XmlDocument { XmlResolver = null, PreserveWhitespace = false };
        document.LoadXml(trimmed);
        var builder = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            NewLineChars = "\n",
            OmitXmlDeclaration = true
        };
        using (var writer = XmlWriter.Create(builder, settings))
        {
            document.Save(writer);
        }

        return builder.ToString();
    }
}
