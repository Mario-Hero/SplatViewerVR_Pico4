// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.IO;
using SMBLibrary;
using SMBLibrary.Client;
using SmbFileAttributes = SMBLibrary.FileAttributes;

/// <summary>A single entry returned by an SMB directory query.</summary>
public sealed class SmbRemoteEntry
{
    public string name;
    public string remotePath;   // relative to the share root, backslash separated
    public bool isDir;
    public long size;
    public DateTime lastWriteUtc;
}

/// <summary>
/// Thin SMB2/SMB3 client on top of SMBLibrary. Each instance owns one TCP
/// connection and is created/disposed inside a worker thread, so no Unity API
/// is touched from background code. Direct TCP on port 445 is used.
/// </summary>
public sealed class SmbShareClient : IDisposable
{
    readonly SMB2Client _client;
    readonly SmbSource _source;
    ISMBFileStore _store;
    bool _loggedIn;

    public SmbShareClient(SmbSource source)
    {
        if (source == null) throw new ArgumentNullException("source");
        _source = source;
        _client = new SMB2Client();
        try
        {
            if (!_client.Connect(source.host, SMBTransportType.DirectTCPTransport))
                throw new IOException("Cannot reach SMB host '" + source.host + "' on port 445.");

            NTStatus status = _client.Login(
                source.domain ?? string.Empty,
                source.username ?? string.Empty,
                source.password ?? string.Empty);
            if (status != NTStatus.STATUS_SUCCESS)
                throw new IOException("SMB login failed (" + status + ") as '" + (source.username ?? "") + "'.");
            _loggedIn = true;

            _store = _client.TreeConnect(source.share, out status);
            if (status != NTStatus.STATUS_SUCCESS || _store == null)
                throw new IOException("Cannot open SMB share '" + source.share + "' (" + status + ").");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Normalize a share-relative path to backslash-separated SMB form.</summary>
    public static string NormalizeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        return path.Replace('/', '\\').Trim('\\');
    }

    public static string CombineRemote(string left, string right)
    {
        string a = NormalizeRemotePath(left);
        string b = NormalizeRemotePath(right);
        if (a.Length == 0) return b;
        if (b.Length == 0) return a;
        return a + "\\" + b;
    }

    /// <summary>
    /// Lists one directory. remoteRelativePath is relative to the configured
    /// start folder (SmbSource.path), not to the share root.
    /// </summary>
    public List<SmbRemoteEntry> ListDirectory(string remoteRelativePath)
    {
        string remotePath = CombineRemote(_source.path, remoteRelativePath);

        object handle;
        FileStatus fileStatus;
        NTStatus status = _store.CreateFile(out handle, out fileStatus, remotePath,
            AccessMask.GENERIC_READ,
            SmbFileAttributes.Directory,
            ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_DIRECTORY_FILE,
            null);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException("Cannot list SMB folder '" + remotePath + "' (" + status + ").");

        try
        {
            List<QueryDirectoryFileInformation> infos;
            status = _store.QueryDirectory(out infos, handle, "*",
                FileInformationClass.FileDirectoryInformation);
            if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_NO_MORE_FILES)
                throw new IOException("SMB directory query failed (" + status + ").");

            var result = new List<SmbRemoteEntry>();
            if (infos != null)
            {
                foreach (var info in infos)
                {
                    var entry = info as FileDirectoryInformation;
                    if (entry == null || string.IsNullOrEmpty(entry.FileName)) continue;
                    if (entry.FileName == "." || entry.FileName == "..") continue;

                    bool isDir = (entry.FileAttributes & SmbFileAttributes.Directory) != 0;
                    DateTime writeUtc = entry.LastWriteTime;
                    if (writeUtc.Kind == DateTimeKind.Local) writeUtc = writeUtc.ToUniversalTime();

                    result.Add(new SmbRemoteEntry
                    {
                        name = entry.FileName,
                        remotePath = remotePath.Length == 0
                            ? entry.FileName
                            : remotePath + "\\" + entry.FileName,
                        isDir = isDir,
                        size = isDir ? 0L : Math.Max(0L, entry.EndOfFile),
                        lastWriteUtc = writeUtc
                    });
                }
            }

            result.Sort(delegate (SmbRemoteEntry a, SmbRemoteEntry b)
            {
                if (a.isDir != b.isDir) return a.isDir ? -1 : 1;
                return string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
            });
            return result;
        }
        finally
        {
            _store.CloseFile(handle);
        }
    }

    /// <summary>
    /// Streams a remote file into destinationPath. remotePath is relative to the
    /// share root (as returned by ListDirectory). Writes to a .part file first so
    /// a failed transfer never leaves a truncated file behind.
    /// </summary>
    public void Download(string remotePath, string destinationPath, long expectedSize,
        Action<long, long> onProgress)
    {
        object handle;
        FileStatus fileStatus;
        NTStatus status = _store.CreateFile(out handle, out fileStatus, NormalizeRemotePath(remotePath),
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            SmbFileAttributes.Normal,
            ShareAccess.Read,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_ALERT,
            null);
        if (status != NTStatus.STATUS_SUCCESS)
            throw new IOException("Cannot open SMB file '" + remotePath + "' (" + status + ").");

        try
        {
            string dir = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string temp = destinationPath + ".part";

            int chunk = (int)Math.Min((long)_client.MaxReadSize, 1024L * 1024L);
            if (chunk <= 0) chunk = 65536;
            long offset = 0L;

            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write,
                FileShare.None, 65536))
            {
                while (true)
                {
                    byte[] data;
                    status = _store.ReadFile(out data, handle, offset, chunk);
                    if (status != NTStatus.STATUS_SUCCESS && status != NTStatus.STATUS_END_OF_FILE)
                        throw new IOException("SMB read failed at offset " + offset + " (" + status + ").");
                    if (status == NTStatus.STATUS_END_OF_FILE || data == null || data.Length == 0)
                        break;

                    stream.Write(data, 0, data.Length);
                    offset += data.Length;
                    if (onProgress != null) onProgress(offset, expectedSize);
                }
            }

            if (File.Exists(destinationPath)) File.Delete(destinationPath);
            File.Move(temp, destinationPath);
        }
        finally
        {
            _store.CloseFile(handle);
        }
    }

    public void Dispose()
    {
        try { if (_store != null) _store.Disconnect(); } catch (Exception) { }
        try { if (_loggedIn) _client.Logoff(); } catch (Exception) { }
        try { _client.Disconnect(); } catch (Exception) { }
        _store = null;
        _loggedIn = false;
    }
}
