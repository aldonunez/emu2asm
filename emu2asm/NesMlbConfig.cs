using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using NumberStyles = System.Globalization.NumberStyles;

namespace emu2asm.NesMlb
{
    public class BankInfo : IXmlSerializable
    {
        public uint Offset;
        public uint Address;
        public uint Size;
        public RomToRamMapping RomToRam;

        public XmlSchema GetSchema() => null;

        public void ReadXml( XmlReader reader )
        {
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
                        Offset = uint.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "Address":
                        content = reader.ReadElementContentAsString();
                        Address = uint.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "Size":
                        content = reader.ReadElementContentAsString();
                        Size = uint.Parse( content, NumberStyles.HexNumber );
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

    public class RomToRamMapping : IXmlSerializable
    {
        public uint RomAddress;
        public uint RamAddress;
        public uint Size;

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
                        RamAddress = uint.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "RomAddress":
                        RomAddress = uint.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "Size":
                        Size = uint.Parse( content, NumberStyles.HexNumber );
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
        public List<BankInfo> Banks;
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
