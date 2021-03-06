﻿using System;
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

    public enum SegmentType
    {
        Program,
        SaveRam,
    }

    public enum MemoryUse
    {
        Mixed,
        Code,
        Data,
    }

    public class Segment : IXmlSerializable
    {
        public SegmentType Type;
        public MemoryUse MemoryUse;

        public Bank Parent;
        public int Id;
        public string Name;

        public string Tag;
        public int Offset;
        public int Address;
        public int Size;

        public int NamespaceBase;
        internal LabelNamespace Namespace;

        internal readonly Dictionary<int, Import> Imports = new();
        internal readonly Dictionary<int, Export> Exports = new();

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

        public int GetRomOffsetFromNSOffset( int nsOffset )
        {
            if ( Type == SegmentType.SaveRam )
                return (nsOffset + 0x6000) - Address + Offset;
            else
                return nsOffset;
        }

        public XmlSchema GetSchema() => null;

        public void ReadXml( XmlReader reader )
        {
            if ( !reader.MoveToFirstAttribute() )
                throw new Exception();

            do
            {
                switch ( reader.Name )
                {
                    case "Tag":
                        Tag = reader.Value;
                        break;

                    case "Type":
                        Type = Enum.Parse<SegmentType>( reader.Value );
                        break;

                    case "Use":
                        MemoryUse = Enum.Parse<MemoryUse>( reader.Value );
                        break;

                    case "Offset":
                        Offset = int.Parse( reader.Value, NumberStyles.HexNumber );
                        break;

                    case "Address":
                        Address = int.Parse( reader.Value, NumberStyles.HexNumber );
                        break;

                    case "Size":
                        Size = int.Parse( reader.Value, NumberStyles.HexNumber );
                        break;
                }
            }
            while ( reader.MoveToNextAttribute() );

            reader.MoveToElement();
            reader.Skip();
        }

        public void WriteXml( XmlWriter writer ) => throw new NotImplementedException();
    }

    public class BlockRef : IXmlSerializable
    {
        [XmlAttribute]
        public string Name;

        public XmlSchema GetSchema() => null;

        public void ReadXml( XmlReader reader )
        {
            Name = reader.GetAttribute( "Name" );
            reader.Skip();
        }

        public void WriteXml( XmlWriter writer ) => throw new NotImplementedException();
    }

    public class Bank : IXmlSerializable
    {
        public string Id;
        public int Offset;
        public int Address;
        public int Size;
        internal readonly List<Segment> Segments = new();
        internal readonly List<BlockRef> Blocks = new();

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

                    case "Segments":
                        ReadSegments( reader );
                        break;

                    case "Block":
                    {
                        var blockRef = new BlockRef();
                        blockRef.ReadXml( reader );
                        Blocks.Add( blockRef );
                    }
                    break;

                    default:
                        reader.Skip();
                        break;
                }
            }

            reader.ReadEndElement();
        }

        private void ReadSegments( XmlReader reader )
        {
            if ( reader.IsEmptyElement )
                return;

            reader.Read();

            while ( reader.NodeType != XmlNodeType.EndElement )
            {
                if ( reader.Name == "Segment" )
                {
                    var segment = new Segment();
                    segment.ReadXml( reader );
                    segment.Parent = this;
                    Segments.Add( segment );
                }
                else
                {
                    reader.Skip();
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

    public class CommentConfig
    {
        public string ProcPattern;
    }

    public class AttributeConfig : IXmlSerializable
    {
        public string Name;
        public string Namespace;
        public int Offset;
        public string Content;

        public XmlSchema GetSchema() => null;

        public void ReadXml( XmlReader reader )
        {
            if ( !reader.MoveToFirstAttribute() )
                throw new Exception();

            do
            {
                switch ( reader.Name )
                {
                    case "Name":
                        Name = reader.Value;
                        break;

                    case "Ns":
                        Namespace = reader.Value;
                        break;

                    case "Offset":
                        Offset = int.Parse( reader.Value, NumberStyles.HexNumber );
                        break;
                }
            }
            while ( reader.MoveToNextAttribute() );

            reader.MoveToElement();

            Content = reader.ReadElementContentAsString();
        }

        public void WriteXml( XmlWriter writer ) => throw new NotImplementedException();
    }

    public class CoverageRange : IXmlSerializable
    {
        public int Offset;
        public int Size;
        public MemoryUse MemoryUse;

        public XmlSchema GetSchema() => null;

        public void ReadXml( XmlReader reader )
        {
            if ( !reader.MoveToFirstAttribute() )
                throw new Exception();

            do
            {
                switch ( reader.Name )
                {
                    case "Offset":
                        Offset = int.Parse( reader.Value, NumberStyles.HexNumber );
                        break;

                    case "Size":
                        Size = int.Parse( reader.Value, NumberStyles.HexNumber );
                        break;

                    case "Use":
                        MemoryUse = Enum.Parse<MemoryUse>( reader.Value );
                        break;

                }
            }
            while ( reader.MoveToNextAttribute() );

            reader.MoveToElement();
            reader.Skip();
        }

        public void WriteXml( XmlWriter writer ) => throw new NotImplementedException();
    }

    public class CodeBlock
    {
        [XmlAttribute]
        public string Name;

        [XmlText]
        public string Content;
    }

    [XmlRoot( ElementName = "Emu2asm-nesmlb-config" )]
    public class Config
    {
        public List<Bank> Banks;
        public LabelMap Labels;
        public CommentConfig Comments;

        [XmlArrayItem( "Attribute" )]
        public List<AttributeConfig> Attributes;

        [XmlArrayItem( "Range" )]
        public List<CoverageRange> Coverage;

        [XmlArrayItem( "Block" )]
        public List<CodeBlock> Code;

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
