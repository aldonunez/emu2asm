using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using System.Xml.Schema;
using System.Xml.Serialization;
using NumberStyles = System.Globalization.NumberStyles;

namespace emu2asm.NesMlb
{
    public struct BankInfo
    {
        public uint Count;
        public uint Size;
    }

    public struct RamToRomMapping : IXmlSerializable
    {
        public uint RamAddress;
        public uint RomAddress;
        public uint Length;

        public XmlSchema GetSchema() => null;

        public void ReadXml( XmlReader reader )
        {
            if ( reader.IsEmptyElement )
                return;

            reader.Read();

            while ( reader.NodeType != XmlNodeType.EndElement )
            {
                string name = reader.Name;
                bool isEmpty = reader.IsEmptyElement;
                string content = "";

                if ( isEmpty )
                    reader.Read();
                else
                    content = reader.ReadElementContentAsString();

                switch ( name )
                {
                    case "RamAddress":
                        RamAddress = uint.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "RomAddress":
                        RomAddress = uint.Parse( content, NumberStyles.HexNumber );
                        break;

                    case "Length":
                        Length = uint.Parse( content, NumberStyles.HexNumber );
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
        public BankInfo BankInfo;

        public RamToRomMapping? RamToRom;

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
