using System.Security.Cryptography;

namespace BlitzRelay.Rooms;

public static class RoomCode
{
	private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

	private const int CodeLength = 8;

	public static string Generate()
	{
		Span<byte> random = stackalloc byte[CodeLength];

		Span<char> buffer = stackalloc char[CodeLength];

		RandomNumberGenerator.Fill(random);

		for (int i = 0; i < CodeLength; i++)
		{
			buffer[i] = Alphabet[random[i] & 31];
		}

		return new string(buffer);
	}
}
