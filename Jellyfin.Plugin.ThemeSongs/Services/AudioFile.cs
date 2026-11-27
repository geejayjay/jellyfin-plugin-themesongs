using System.IO;

namespace Jellyfin.Plugin.ThemeSongs.Services
{
    /// <summary>Small file checks shared by downloads and uploads.</summary>
    public static class AudioFile
    {
        /// <summary>True when the file starts like an MP3: an ID3 tag or an MPEG audio frame header.</summary>
        public static bool LooksLikeMp3(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                return LooksLikeMp3(fs);
            }
            catch (IOException)
            {
                return false;
            }
        }

        public static bool LooksLikeMp3(Stream stream)
        {
            var head = new byte[3];
            int n = stream.Read(head, 0, 3);
            if (stream.CanSeek)
            {
                stream.Seek(0, SeekOrigin.Begin);
            }

            if (n < 3)
            {
                return false;
            }

            if (head[0] == (byte)'I' && head[1] == (byte)'D' && head[2] == (byte)'3')
            {
                return true;
            }

            // MPEG frame sync: 11 set bits, layer bits not "reserved".
            return head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && (head[1] & 0x06) != 0;
        }
    }
}
