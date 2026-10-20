using System.Runtime.InteropServices;
using NexusMods.Sdk.Native;

namespace NexusMods.Deployment.Fuse.Native;

/// <summary>
/// The libfuse3 low-level API, bound to the symbols the library actually exports. The C headers hide several
/// of those behind macros, so binding the header name would bind the wrong ABI (or nothing). Where the exported
/// name differs from the C name, or the symbol carries a version, the table says so (checked against
/// <c>nm -D --defined-only /usr/lib64/libfuse3.so.4</c> on libfuse 3.18.3).
/// <code>
/// C name                             exported name                      since
/// fuse_session_new                   fuse_session_new_versioned         3.17
/// fuse_session_mount                 fuse_session_mount                 3.0
/// fuse_session_loop_mt               fuse_session_loop_mt_312           3.12
/// fuse_loop_cfg_create               fuse_loop_cfg_create               3.12
/// fuse_loop_cfg_destroy              fuse_loop_cfg_destroy              3.12
/// fuse_loop_cfg_set_clone_fd         fuse_loop_cfg_set_clone_fd         3.12
/// fuse_loop_cfg_set_max_threads      fuse_loop_cfg_set_max_threads      3.12
/// fuse_session_exit                  fuse_session_exit                  3.0
/// fuse_session_unmount               fuse_session_unmount               3.0
/// fuse_session_destroy               fuse_session_destroy               3.0
/// fuse_session_fd                    fuse_session_fd                    3.0
/// fuse_reply_err                     fuse_reply_err                     3.0
/// fuse_reply_none                    fuse_reply_none                    3.0
/// fuse_reply_entry                   fuse_reply_entry                   3.0
/// fuse_reply_attr                    fuse_reply_attr                    3.0
/// fuse_reply_readlink                fuse_reply_readlink                3.0
/// fuse_reply_open                    fuse_reply_open                    3.0
/// fuse_reply_create                  fuse_reply_create                  3.0
/// fuse_reply_write                   fuse_reply_write                   3.0
/// fuse_reply_buf                     fuse_reply_buf                     3.0
/// fuse_reply_data                    fuse_reply_data                    3.0
/// fuse_reply_statfs                  fuse_reply_statfs                  3.0
/// fuse_add_direntry                  fuse_add_direntry                  3.0
/// fuse_add_direntry_plus             fuse_add_direntry_plus             3.0
/// fuse_req_userdata                  fuse_req_userdata                  3.0
/// fuse_req_ctx                       fuse_req_ctx                       3.0
/// fuse_lowlevel_notify_inval_inode   fuse_lowlevel_notify_inval_inode   3.0
/// fuse_lowlevel_notify_inval_entry   fuse_lowlevel_notify_inval_entry   3.0
/// fuse_passthrough_open              fuse_passthrough_open              3.17
/// fuse_passthrough_close             fuse_passthrough_close             3.17
/// fuse_set_feature_flag              fuse_set_feature_flag              3.17.3
/// fuse_get_feature_flag              fuse_get_feature_flag              3.17.3
/// fuse_buf_size                      fuse_buf_size                      3.0
/// </code>
/// <c>fuse_session_new</c> is a header macro over <c>fuse_session_new_versioned</c> from 3.17; the plain
/// <c>fuse_session_new@@FUSE_3.0</c> export is the old ABI and is deliberately not bound. <c>fuse_session_loop_mt</c>
/// has three versioned exports (3.0, 3.2, 3.12) behind the same name, so the 3.12 one is pinned by its own name.
/// <c>fuse_conn_info.want</c> gave way to <c>want_ext</c> in 3.17, set through the feature-flag exports.
/// Pointers are raw because the callers fill pinned or stack structs; strings are UTF-8 bytes the caller owns.
/// </summary>
internal static unsafe partial class LibFuse3LowLevel
{
    private const string Lib = LibFuse3.LibraryName;

    static LibFuse3LowLevel()
    {
        StructGuard.RequireX64();
        LibFuse3Resolver.Install();
    }

    private const string SessionNewVersionedSymbol = "fuse_session_new_versioned";
    private const string SessionMountSymbol = "fuse_session_mount";
    private const string SessionLoopMtSymbol = "fuse_session_loop_mt_312";
    private const string LoopCfgCreateSymbol = "fuse_loop_cfg_create";
    private const string LoopCfgDestroySymbol = "fuse_loop_cfg_destroy";
    private const string LoopCfgSetCloneFdSymbol = "fuse_loop_cfg_set_clone_fd";
    private const string LoopCfgSetMaxThreadsSymbol = "fuse_loop_cfg_set_max_threads";
    private const string SessionExitSymbol = "fuse_session_exit";
    private const string SessionUnmountSymbol = "fuse_session_unmount";
    private const string SessionDestroySymbol = "fuse_session_destroy";
    private const string SessionFdSymbol = "fuse_session_fd";
    private const string ReplyErrSymbol = "fuse_reply_err";
    private const string ReplyNoneSymbol = "fuse_reply_none";
    private const string ReplyEntrySymbol = "fuse_reply_entry";
    private const string ReplyAttrSymbol = "fuse_reply_attr";
    private const string ReplyReadlinkSymbol = "fuse_reply_readlink";
    private const string ReplyOpenSymbol = "fuse_reply_open";
    private const string ReplyCreateSymbol = "fuse_reply_create";
    private const string ReplyWriteSymbol = "fuse_reply_write";
    private const string ReplyBufSymbol = "fuse_reply_buf";
    private const string ReplyDataSymbol = "fuse_reply_data";
    private const string ReplyStatfsSymbol = "fuse_reply_statfs";
    private const string AddDirentrySymbol = "fuse_add_direntry";
    private const string AddDirentryPlusSymbol = "fuse_add_direntry_plus";
    private const string ReqUserdataSymbol = "fuse_req_userdata";
    private const string ReqCtxSymbol = "fuse_req_ctx";
    private const string NotifyInvalInodeSymbol = "fuse_lowlevel_notify_inval_inode";
    private const string NotifyInvalEntrySymbol = "fuse_lowlevel_notify_inval_entry";
    private const string PassthroughOpenSymbol = "fuse_passthrough_open";
    private const string PassthroughCloseSymbol = "fuse_passthrough_close";
    private const string SetFeatureFlagSymbol = "fuse_set_feature_flag";
    private const string GetFeatureFlagSymbol = "fuse_get_feature_flag";
    private const string BufSizeSymbol = "fuse_buf_size";

    /// <summary>Every exported name bound here, so a test can fail on a renamed export before the first mount.</summary>
    internal static readonly string[] EntryPoints =
    [
        SessionNewVersionedSymbol,
        SessionMountSymbol,
        SessionLoopMtSymbol,
        LoopCfgCreateSymbol,
        LoopCfgDestroySymbol,
        LoopCfgSetCloneFdSymbol,
        LoopCfgSetMaxThreadsSymbol,
        SessionExitSymbol,
        SessionUnmountSymbol,
        SessionDestroySymbol,
        SessionFdSymbol,
        ReplyErrSymbol,
        ReplyNoneSymbol,
        ReplyEntrySymbol,
        ReplyAttrSymbol,
        ReplyReadlinkSymbol,
        ReplyOpenSymbol,
        ReplyCreateSymbol,
        ReplyWriteSymbol,
        ReplyBufSymbol,
        ReplyDataSymbol,
        ReplyStatfsSymbol,
        AddDirentrySymbol,
        AddDirentryPlusSymbol,
        ReqUserdataSymbol,
        ReqCtxSymbol,
        NotifyInvalInodeSymbol,
        NotifyInvalEntrySymbol,
        PassthroughOpenSymbol,
        PassthroughCloseSymbol,
        SetFeatureFlagSymbol,
        GetFeatureFlagSymbol,
        BufSizeSymbol,
    ];

    [LibraryImport(Lib, EntryPoint = SessionNewVersionedSymbol)]
    internal static partial IntPtr fuse_session_new(FuseArgs* args, FuseLowlevelOps* ops, nuint opSize, LibfuseVersion* version, IntPtr userdata);

    [LibraryImport(Lib, EntryPoint = SessionMountSymbol, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int fuse_session_mount(IntPtr session, string mountpoint);

    [LibraryImport(Lib, EntryPoint = SessionLoopMtSymbol)]
    internal static partial int fuse_session_loop_mt(IntPtr session, IntPtr config);

    [LibraryImport(Lib, EntryPoint = LoopCfgCreateSymbol)]
    internal static partial IntPtr fuse_loop_cfg_create();

    [LibraryImport(Lib, EntryPoint = LoopCfgDestroySymbol)]
    internal static partial void fuse_loop_cfg_destroy(IntPtr config);

    [LibraryImport(Lib, EntryPoint = LoopCfgSetCloneFdSymbol)]
    internal static partial void fuse_loop_cfg_set_clone_fd(IntPtr config, uint value);

    [LibraryImport(Lib, EntryPoint = LoopCfgSetMaxThreadsSymbol)]
    internal static partial void fuse_loop_cfg_set_max_threads(IntPtr config, uint value);

    [LibraryImport(Lib, EntryPoint = SessionExitSymbol)]
    internal static partial void fuse_session_exit(IntPtr session);

    [LibraryImport(Lib, EntryPoint = SessionUnmountSymbol)]
    internal static partial void fuse_session_unmount(IntPtr session);

    [LibraryImport(Lib, EntryPoint = SessionDestroySymbol)]
    internal static partial void fuse_session_destroy(IntPtr session);

    [LibraryImport(Lib, EntryPoint = SessionFdSymbol)]
    internal static partial int fuse_session_fd(IntPtr session);

    [LibraryImport(Lib, EntryPoint = ReplyErrSymbol)]
    internal static partial int fuse_reply_err(IntPtr req, int err);

    [LibraryImport(Lib, EntryPoint = ReplyNoneSymbol)]
    internal static partial void fuse_reply_none(IntPtr req);

    [LibraryImport(Lib, EntryPoint = ReplyEntrySymbol)]
    internal static partial int fuse_reply_entry(IntPtr req, FuseEntryParam* entry);

    [LibraryImport(Lib, EntryPoint = ReplyAttrSymbol)]
    internal static partial int fuse_reply_attr(IntPtr req, LibcStat.Buffer* attr, double timeout);

    [LibraryImport(Lib, EntryPoint = ReplyReadlinkSymbol)]
    internal static partial int fuse_reply_readlink(IntPtr req, byte* link);

    [LibraryImport(Lib, EntryPoint = ReplyOpenSymbol)]
    internal static partial int fuse_reply_open(IntPtr req, FuseFileInfo* info);

    [LibraryImport(Lib, EntryPoint = ReplyCreateSymbol)]
    internal static partial int fuse_reply_create(IntPtr req, FuseEntryParam* entry, FuseFileInfo* info);

    [LibraryImport(Lib, EntryPoint = ReplyWriteSymbol)]
    internal static partial int fuse_reply_write(IntPtr req, nuint count);

    [LibraryImport(Lib, EntryPoint = ReplyBufSymbol)]
    internal static partial int fuse_reply_buf(IntPtr req, byte* buffer, nuint size);

    [LibraryImport(Lib, EntryPoint = ReplyDataSymbol)]
    internal static partial int fuse_reply_data(IntPtr req, FuseBufvec* bufv, FuseBufCopyFlags flags);

    [LibraryImport(Lib, EntryPoint = ReplyStatfsSymbol)]
    internal static partial int fuse_reply_statfs(IntPtr req, StatVfs* stat);

    [LibraryImport(Lib, EntryPoint = AddDirentrySymbol)]
    internal static partial nuint fuse_add_direntry(IntPtr req, byte* buffer, nuint bufferSize, byte* name, LibcStat.Buffer* attr, long nextOffset);

    [LibraryImport(Lib, EntryPoint = AddDirentryPlusSymbol)]
    internal static partial nuint fuse_add_direntry_plus(IntPtr req, byte* buffer, nuint bufferSize, byte* name, FuseEntryParam* entry, long nextOffset);

    [LibraryImport(Lib, EntryPoint = ReqUserdataSymbol)]
    internal static partial IntPtr fuse_req_userdata(IntPtr req);

    [LibraryImport(Lib, EntryPoint = ReqCtxSymbol)]
    internal static partial FuseCtx* fuse_req_ctx(IntPtr req);

    [LibraryImport(Lib, EntryPoint = NotifyInvalInodeSymbol)]
    internal static partial int fuse_lowlevel_notify_inval_inode(IntPtr session, ulong ino, long offset, long length);

    [LibraryImport(Lib, EntryPoint = NotifyInvalEntrySymbol)]
    internal static partial int fuse_lowlevel_notify_inval_entry(IntPtr session, ulong parent, byte* name, nuint nameLength);

    [LibraryImport(Lib, EntryPoint = PassthroughOpenSymbol)]
    internal static partial int fuse_passthrough_open(IntPtr req, int fd);

    [LibraryImport(Lib, EntryPoint = PassthroughCloseSymbol)]
    internal static partial int fuse_passthrough_close(IntPtr req, int backingId);

    [LibraryImport(Lib, EntryPoint = SetFeatureFlagSymbol)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool fuse_set_feature_flag(FuseConnInfo* conn, ulong flag);

    [LibraryImport(Lib, EntryPoint = GetFeatureFlagSymbol)]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool fuse_get_feature_flag(FuseConnInfo* conn, ulong flag);

    [LibraryImport(Lib, EntryPoint = BufSizeSymbol)]
    internal static partial nuint fuse_buf_size(FuseBufvec* bufv);
}
