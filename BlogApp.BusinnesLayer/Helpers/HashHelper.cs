using System.Security.Cryptography;

namespace BlogApp.BusinnesLayer.Helpers;

public static class HashHelper
{
    private const int SaltByteSize = 24;
    private const int HashByteSize = 24;
    private const int HasingIterationsCount = 10101;
    public static string HashPassword(string password)
    {
        byte[] salt;
        byte[] buffer2;
        if (password == null)
        {
            throw new ArgumentNullException("password");
        }
        using (Rfc2898DeriveBytes bytes = new Rfc2898DeriveBytes(password, SaltByteSize, HasingIterationsCount))
        {
            salt = bytes.Salt;
            buffer2 = bytes.GetBytes(HashByteSize);
        }
        byte[] dst = new byte[(SaltByteSize + HashByteSize) + 1];
        Buffer.BlockCopy(salt, 0, dst, 1, SaltByteSize);
        Buffer.BlockCopy(buffer2, 0, dst, SaltByteSize + 1, HashByteSize);
        return Convert.ToBase64String(dst);
    }

    public static bool VerifyHashedPassword(string hashedPassword, string password)
    {
        byte[] _passwordHashBytes;

        int _arrayLen = (SaltByteSize + HashByteSize) + 1;

        if (hashedPassword == null)
        {
            return false;
        }

        if (password == null)
        {
            throw new ArgumentNullException("password");
        }

        byte[] src = Convert.FromBase64String(hashedPassword);

        if ((src.Length != _arrayLen) || (src[0] != 0))
        {
            return false;
        }

        byte[] _currentSaltBytes = new byte[SaltByteSize];
        Buffer.BlockCopy(src, 1, _currentSaltBytes, 0, SaltByteSize);

        byte[] _currentHashBytes = new byte[HashByteSize];
        Buffer.BlockCopy(src, SaltByteSize + 1, _currentHashBytes, 0, HashByteSize);

        using (Rfc2898DeriveBytes bytes = new Rfc2898DeriveBytes(password, _currentSaltBytes, HasingIterationsCount))
        {
            _passwordHashBytes = bytes.GetBytes(SaltByteSize);
        }

        return AreHashesEqual(_currentHashBytes, _passwordHashBytes);

    }
    private static bool AreHashesEqual(byte[] firstHash, byte[] secondHash)
    {
        int _minHashLength = firstHash.Length <= secondHash.Length ? firstHash.Length : secondHash.Length;
        var xor = firstHash.Length ^ secondHash.Length;
        for (int i = 0; i < _minHashLength; i++)
            xor |= firstHash[i] ^ secondHash[i];
        return 0 == xor;
    }
    //    const int keySize = 64;
    //    const int iterations = 350000;
    //    static HashAlgorithmName hashAlgorithm = HashAlgorithmName.SHA512;
    //    public static string HashPasword(string password)
    //    {
    //        byte[] salt = Encoding.UTF8.GetBytes("Mysha");
    //        var hash = Rfc2898DeriveBytes.Pbkdf2(
    //            Encoding.UTF8.GetBytes(password),
    //            salt,
    //            iterations,
    //            hashAlgorithm,
    //            keySize);
    //        return Convert.ToHexString(hash);
    //    }
    //    public static bool VerifyPassword(string password, string hash)
    //    {
    //        byte[] salt = Encoding.UTF8.GetBytes("Mysha");
    //        var hashToCompare = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, hashAlgorithm, keySize);
    //        return CryptographicOperations.FixedTimeEquals(hashToCompare, Convert.FromHexString(hash));
    //    }




    //public static byte[] SHA256HASHByte(string value)
    //{
    //    using (SHA256 sha256 = SHA256.Create())
    //    {
    //        byte[] inputBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(value));
    //        return inputBytes;
    //    }
    //}
}
