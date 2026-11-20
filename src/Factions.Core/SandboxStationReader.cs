using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;

namespace Factions.Core
{
    /// <summary>
    /// Reads serialized economy-station XML only from one exact faction in a Sandbox checkpoint.
    /// This has no SE/Torch dependency so the cursor contract can be regression-tested in Core.
    /// </summary>
    public static class SandboxStationReader
    {
        public static IEnumerable<XmlNode> ReadFactionStations(TextReader input, string factionTag)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (string.IsNullOrEmpty(factionTag)) throw new ArgumentException("faction tag is required", nameof(factionTag));

            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Ignore,
                IgnoreComments = true,
                IgnoreWhitespace = true
            };
            using (XmlReader reader = XmlReader.Create(input, settings))
            {
                bool inFaction = false;
                bool isTargetFaction = false;
                int factionDepth = -1;
                while (!reader.EOF)
                {
                    if (reader.NodeType == XmlNodeType.Element && reader.Name == "MyObjectBuilder_Faction")
                    {
                        inFaction = true;
                        isTargetFaction = false;
                        factionDepth = reader.Depth;
                    }
                    else if (inFaction && reader.NodeType == XmlNodeType.EndElement
                        && reader.Name == "MyObjectBuilder_Faction" && reader.Depth == factionDepth)
                    {
                        inFaction = false;
                        isTargetFaction = false;
                    }
                    else if (inFaction && reader.NodeType == XmlNodeType.Element
                        && reader.Name == "Tag" && reader.Depth == factionDepth + 1)
                    {
                        isTargetFaction = string.Equals(reader.ReadElementContentAsString(), factionTag,
                            StringComparison.OrdinalIgnoreCase);
                        continue; // ReadElementContentAsString already advanced the shared reader.
                    }
                    else if (isTargetFaction && reader.NodeType == XmlNodeType.Element
                        && reader.Name == "MyObjectBuilder_Station")
                    {
                        var document = new XmlDocument();
                        XmlNode station = document.ReadNode(reader);
                        if (station != null) yield return station;
                        continue; // ReadNode already advanced the shared reader to the next sibling.
                    }
                    reader.Read();
                }
            }
        }
    }
}
