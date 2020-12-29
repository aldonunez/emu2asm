using System.IO;
using System.Xml;

namespace emu2asm.NesMlb
{
    static class Processor
    {
        public static void Disassemble(
            string configPath,
            string romPath,
            string coveragePath,
            string labelPath )
        {
            var config = ReadConfig( configPath );
            var romImage = ReadRomImage( romPath );
            var coverage = ReadCoverageImage( coveragePath );
            var labelDb = ReadLabelDb( labelPath );

            var disassembler = new Disassembler(
                configPath,
                romPath,
                coveragePath,
                labelPath );
        }

        private static Config ReadConfig( string path )
        {
            var settings = new XmlReaderSettings
            {
                XmlResolver = null
            };

            using ( StreamReader streamReader = new StreamReader( path ) )
            {
                return Config.Make( streamReader );
            }
        }

        private static object ReadLabelDb( string path )
        {
            using ( StreamReader streamReader = new StreamReader( path ) )
            {
                return LabelDatabase.Make( streamReader );
            }
        }

        private static Rom ReadRomImage( string romPath )
        {
            using ( var stream = File.OpenRead( romPath ) )
            {
                return Rom.Make( stream );
            }
        }

        private static byte[] ReadCoverageImage( string path )
        {
            return File.ReadAllBytes( path );
        }

        public static void UpdateConfig(
            string configPath,
            string romPath )
        {
        }
    }
}
