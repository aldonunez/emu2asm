using System;
using System.IO;
using System.Security.Cryptography;

namespace emu2asm.NesMlb
{
    class Rom
    {
        private const int HeaderLength = 16;

        public string FileHash;
        public string ImageHash;
        public byte[] Header;
        public byte[] Image;

        public static Rom Make( Stream stream )
        {
            Rom rom = new Rom();
            long origPosition = stream.Position;
            long length = stream.Length - origPosition;

            if ( length < HeaderLength || length > int.MaxValue )
                throw new ApplicationException();

            using var hashAlgo = SHA1.Create();

            byte[] fileHash = hashAlgo.ComputeHash( stream );

            stream.Position = origPosition;

            byte[] header = new byte[HeaderLength];
            stream.Read( header, 0, HeaderLength );

            int imageLength = (int) (length - HeaderLength);
            byte[] image = new byte[imageLength];
            stream.Read( image, 0, imageLength );

            byte[] imageHash = hashAlgo.ComputeHash( image );
            rom.FileHash = Convert.ToHexString( fileHash );
            rom.ImageHash = Convert.ToHexString( imageHash );
            rom.Image = image;
            rom.Header = header;

            return rom;
        }
    }
}
