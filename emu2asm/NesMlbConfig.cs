using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using NumberStyles = System.Globalization.NumberStyles;

namespace emu2asm.NesMlb
{
    internal record Import
    {
        public LabelRecord Label;
        public Segment Source;

        public Import( LabelRecord label, Segment source )
        {
            Label = label;
            Source = source;
        }
    }

    internal record Export
    {
        public LabelRecord Label;

        public Export( LabelRecord label )
        {
            Label = label;
        }
    }

    internal enum SegmentType
    {
        Program,
        SaveRam,
    }

    internal record Segment
    {
        public SegmentType Type;
        public Bank Parent;
        public int Id;
        public string Name;
        public int Offset;
        public int Address;
        public int Size;
        public int NamespaceBase;
        public LabelNamespace Namespace;

        public readonly Dictionary<int, Import> Imports = new();
        public readonly Dictionary<int, Export> Exports = new();

        public bool IsAddressInside( int address )
        {
            return (address >= Address) && (address < Address + Size);
        }

        public int GetAddress( int offset )
        {
            return (offset - Offset) + Address;
        }

        public int GetNamespaceOffset( int offset )
        {
            return offset - NamespaceBase;
        }
    }

    public class Bank : IXmlSerializable
    {
        public string Id;
        public int Offset;
        public int Address;
        public int Size;
        public RomToRamMapping RomToRam;
        internal readonly List<Segment> Segments = new();

        public XmlSchema GetSchema() => null;

        public void ReadXml( XmlReader reader )
        {
            Id = reader.GetAttribute( "Id" );

            if ( reader.IsEmptyElement )
                return;

            reader.Read();

            while ( reader.NodeType != XmlNodeType.EndElement )
            {
                string name = reader.Name;
                string content;

                switch ( name )
                {
                    case "Offset":
                        content = reader.ReadElementContentAsString();
                        Offset = int.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "Address":
                        content = reader.ReadElementContentAsString();
                        Address = int.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "Size":
                        content = reader.ReadElementContentAsString();
                        Size = int.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "RomToRam":
                    {
                        var romToRam = new RomToRamMapping();
                        romToRam.ReadXml( reader );
                        RomToRam = romToRam;
                        break;
                    }
                }
            }

            reader.ReadEndElement();
        }

        public void WriteXml( XmlWriter writer ) => throw new NotImplementedException();
    }

    public enum MemoryUse
    {
        Mixed,
        Code,
        Data,
    }

    public class RomToRamMapping : IXmlSerializable
    {
        public int RomAddress;
        public int RamAddress;
        public int Size;
        public MemoryUse Type;

        public XmlSchema GetSchema() => null;

        public void ReadXml( XmlReader reader )
        {
            if ( reader.IsEmptyElement )
                return;

            reader.Read();

            while ( reader.NodeType != XmlNodeType.EndElement )
            {
                string name = reader.Name;
                string content = reader.ReadElementContentAsString();

                switch ( name )
                {
                    case "RamAddress":
                        RamAddress = int.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "RomAddress":
                        RomAddress = int.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "Size":
                        Size = int.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "Type":
                        Type = Enum.Parse<MemoryUse>( content, true );
                        break;
                }
            }

            reader.ReadEndElement();
        }

        public void WriteXml( XmlWriter writer ) => throw new NotImplementedException();
    }

    public class LabelMap : Dictionary<string, uint>, IXmlSerializable
    {
        public XmlSchema GetSchema() => null;

        public void ReadXml( XmlReader reader )
        {
            if ( reader.IsEmptyElement )
                return;

            reader.Read();

            while ( reader.NodeType != XmlNodeType.EndElement )
            {
                if ( reader.Name == "Label" )
                {
                    string name = reader.GetAttribute( "Name" );
                    string sValue = reader.GetAttribute( "Value" );
                    uint uValue = uint.Parse( sValue, NumberStyles.HexNumber );

                    this.Add( name, uValue );
                }

                reader.ReadElementContentAsString();
            }

            reader.ReadEndElement();
        }

        public void WriteXml( XmlWriter writer ) => throw new NotImplementedException();
    }

    [XmlRoot( ElementName = "Emu2asm-nesmlb-config" )]
    public class Config
    {
        public List<Bank> Banks;
        public LabelMap Labels;

        public static Config Make( TextReader textReader )
        {
            var settings = new XmlReaderSettings
            {
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                IgnoreWhitespace = true,
            };

            var xmlReader = XmlReader.Create( textReader, settings );
            var serializer = new XmlSerializer( typeof( Config ) );

            return (Config) serializer.Deserialize( xmlReader );
        }
    }
}
