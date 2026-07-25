using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;


// Reads the non-text clipboard formats: files copied in Explorer, and raw bitmap data from a
// screenshot (Win+Shift+S). TextCopy handles text; neither it nor the console gives us these, so
// they are pulled straight from the Win32 clipboard.
//
// Everything here is best-effort. The clipboard is a shared, racy resource — another process can
// hold it open — and a failed read simply means the paste falls through to its text behavior.
internal static class ClipboardMedia
{
	private const uint CF_DIB = 8;
	private const uint CF_HDROP = 15;

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool OpenClipboard(IntPtr hWndNewOwner);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool CloseClipboard();

	[DllImport("user32.dll", SetLastError = true)]
	private static extern IntPtr GetClipboardData(uint uFormat);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool IsClipboardFormatAvailable(uint format);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern IntPtr GlobalLock(IntPtr hMem);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool GlobalUnlock(IntPtr hMem);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern UIntPtr GlobalSize(IntPtr hMem);

	[DllImport("shell32.dll", CharSet = CharSet.Unicode)]
	private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

	// Paths of files copied in Explorer, empty when the clipboard holds none.
	internal static List<string> GetFiles()
	{
		List<string> files = new List<string>();
		if (!IsClipboardFormatAvailable(CF_HDROP))
			return files;
		if (!OpenClipboard(IntPtr.Zero))
			return files;

		try
		{
			IntPtr drop = GetClipboardData(CF_HDROP);
			if (drop == IntPtr.Zero)
				return files;

			uint count = DragQueryFileW(drop, 0xFFFFFFFF, null, 0);
			for (uint i = 0; i < count; i++)
			{
				uint length = DragQueryFileW(drop, i, null, 0);
				StringBuilder path = new StringBuilder((int)length + 1);
				if (DragQueryFileW(drop, i, path, (uint)path.Capacity) > 0)
				{
					string value = path.ToString();
					if (File.Exists(value))
						files.Add(value);
				}
			}
		}
		catch (Exception)
		{
			files.Clear();
		}
		finally
		{
			CloseClipboard();
		}
		return files;
	}

	// Saves clipboard bitmap data into folder as a PNG and returns its full path, or empty when the
	// clipboard holds no image. PNG because it is the format every vision model accepts — the raw
	// clipboard DIB (and a plain .bmp) is rejected by most of them.
	internal static string SaveImage(string folder)
	{
		if (!IsClipboardFormatAvailable(CF_DIB))
			return string.Empty;
		if (!OpenClipboard(IntPtr.Zero))
			return string.Empty;

		byte[]? dib = null;
		try
		{
			IntPtr handle = GetClipboardData(CF_DIB);
			if (handle == IntPtr.Zero)
				return string.Empty;

			IntPtr data = GlobalLock(handle);
			if (data == IntPtr.Zero)
				return string.Empty;

			try
			{
				int size = (int)GlobalSize(handle).ToUInt64();
				if (size <= 40)
					return string.Empty;
				dib = new byte[size];
				Marshal.Copy(data, dib, 0, size);
			}
			finally
			{
				GlobalUnlock(handle);
			}
		}
		catch (Exception)
		{
			return string.Empty;
		}
		finally
		{
			CloseClipboard();
		}

		// The whole file is Win32 clipboard access, so this is never false — the check is what
		// tells the platform analyzer that the imaging calls below are Windows-only.
		if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1))
			return string.Empty;

		try
		{
			byte[] bmp = WrapDibAsBitmapFile(dib!);
			using MemoryStream stream = new MemoryStream(bmp);
			using Image image = Image.FromStream(stream);
			Directory.CreateDirectory(folder);
			string path = Path.Combine(folder, $"clipboard-{Guid.NewGuid():N}.png");
			image.Save(path, ImageFormat.Png);
			return path;
		}
		catch (Exception)
		{
			return string.Empty;
		}
	}

	// A clipboard DIB is a BITMAPINFOHEADER, optional colour masks/palette, then pixels — the same
	// bytes a .bmp file carries after its 14-byte file header. Prepending that header turns the
	// blob into something the image decoder will read.
	private static byte[] WrapDibAsBitmapFile(byte[] dib)
	{
		int headerSize = BitConverter.ToInt32(dib, 0);
		short bitCount = BitConverter.ToInt16(dib, 14);
		int compression = BitConverter.ToInt32(dib, 16);
		int clrUsed = BitConverter.ToInt32(dib, 32);

		// Colour table: explicit count when given, otherwise the full table implied by the depth
		// (none at all above 8 bits per pixel).
		int paletteEntries = clrUsed;
		if (paletteEntries == 0 && bitCount <= 8)
			paletteEntries = 1 << bitCount;

		int offset = 14 + headerSize + paletteEntries * 4;
		// BI_BITFIELDS (3) puts three colour masks between the header and the pixels.
		if (compression == 3)
			offset += 12;

		byte[] file = new byte[14 + dib.Length];
		file[0] = (byte)'B';
		file[1] = (byte)'M';
		BitConverter.GetBytes(file.Length).CopyTo(file, 2);
		BitConverter.GetBytes(offset).CopyTo(file, 10);
		dib.CopyTo(file, 14);
		return file;
	}
}
