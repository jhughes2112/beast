// clockfs: mounts over a folder and passes everything through to it, except that a change takes its
// time from a clock file instead of the real clock. Beast mounts it for seeded runs, so a listing reads
// the same on every run: the kernel stamps files with the real clock, and no program can be told otherwise.
//
//   clockfs <folder> <clock file holding unix seconds>
//
// The folder is opened before the mount, so the real files stay reachable underneath it. Times are
// written to the real file, not held here: they survive an unmount and the host sees them. A program
// that sets a time itself (touch -d, cp -p, tar) keeps that time. Every time a file reports is its
// modification time, so `ls -lc` and `ls -lu` show nothing real either.
//
//   clockfs --owner <uid> <gid> <folder>
//
// The sandbox's ownership instead of a clock, mounted over the workspace at startup (Sandbox.cs). Times
// are real. Ownership is reported, not stored: everything belongs to uid:gid, except a top-level
// dot-folder, dot-symlink or .git that is really root's, which reads as root's all the way down; nothing
// has a group or other write bit, so only the owner writes. The kernel checks permissions against what is reported, so the lockdown
// only has to chown the top-level entries instead of every file, which on Docker Desktop's Windows bind
// mounts took minutes per start. A hard link to a sealed file is refused: under another name it would
// read as uid's.
#define FUSE_USE_VERSION 31
#define _GNU_SOURCE

#include <errno.h>
#include <fcntl.h>
#include <fuse3/fuse.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/statvfs.h>
#include <sys/time.h>
#include <sys/types.h>
#include <sys/xattr.h>
#include <dirent.h>
#include <unistd.h>

static int         g_root  = -1;
static int         g_clock = -1;
static int         g_owner = 0;
static uid_t       g_uid   = 0;
static gid_t       g_gid   = 0;

// The path relative to the real folder; the folder itself is ".".
static const char *rel(const char *path)
{
	return path[1] == '\0' ? "." : path + 1;
}

// Whether a path is inside (or is) a sealed top-level entry: a dot-folder, dot-symlink or .git that
// is really root's. Only a path whose first name starts with '.' costs a look at that entry.
static int is_sealed(const char *path)
{
	char        top[NAME_MAX + 1];
	const char *name  = path + 1;
	const char *slash = strchr(name, '/');
	size_t      len   = slash != NULL ? (size_t)(slash - name) : strlen(name);
	struct stat st;
	if (name[0] != '.' || len == 0 || len > NAME_MAX)
		return 0;
	memcpy(top, name, len);
	top[len] = '\0';
	if (fstatat(g_root, top, &st, AT_SYMLINK_NOFOLLOW) != 0 || st.st_uid != 0)
		return 0;
	return S_ISDIR(st.st_mode) || S_ISLNK(st.st_mode) || strcmp(top, ".git") == 0;
}

// What the kernel checks permissions against in owner mode. The mount's own folder reports as it is.
// Nothing reports a group or other write bit: only its owner may change a file. A host mount reads as
// writable by all (Docker Desktop's Windows binds report 0777), and the sandbox's read-only shell user,
// which is neither the owner nor in its group, must not be able to write the workspace through that.
// What the owner may read or enter, group and other may too: the read-only user reads everything the
// shell user can, whatever modes a file carries on the host (an earlier lockdown's main checkout leaves
// its folders 0711 and its files owner-only, stored on the host and seen by every container after).
// A sealed entry keeps its own read bits, so a folder kept from the shell user stays kept from both.
static void report_owner(const char *path, struct stat *st)
{
	if (path[1] == '\0')
		return;
	if (is_sealed(path))
	{
		st->st_uid = 0;
		st->st_gid = 0;
	}
	else
	{
		mode_t readable = st->st_mode & (S_IRUSR | S_IXUSR);
		st->st_uid      = g_uid;
		st->st_gid      = g_gid;
		st->st_mode     = (st->st_mode & ~(mode_t)(S_IRWXG | S_IRWXO)) | (readable >> 3) | (readable >> 6);
	}
	st->st_mode &= ~(mode_t)(S_IWGRP | S_IWOTH);
}

static time_t now(void)
{
	char    text[32];
	ssize_t got = pread(g_clock, text, sizeof(text) - 1, 0);
	if (got <= 0)
		return time(NULL);
	text[got] = '\0';
	return (time_t)strtoll(text, NULL, 10);
}

// Owner mode has no clock: a change keeps the time the kernel gave it.
static void stamp(const char *path)
{
	struct timespec times[2];
	if (g_clock < 0)
		return;
	times[0].tv_sec  = times[1].tv_sec  = now();
	times[0].tv_nsec = times[1].tv_nsec = 0;
	utimensat(g_root, rel(path), times, AT_SYMLINK_NOFOLLOW);
}

// A new or removed entry changes its folder too.
static void stamp_parent(const char *path)
{
	char  parent[PATH_MAX];
	char *slash;
	snprintf(parent, sizeof(parent), "%s", path);
	slash = strrchr(parent, '/');
	if (slash == parent)
		parent[1] = '\0';
	else if (slash != NULL)
		*slash = '\0';
	stamp(parent);
}

// New entries belong to whoever asked for them, not to this root daemon.
static void own(const char *path)
{
	struct fuse_context *ctx = fuse_get_context();
	(void)!fchownat(g_root, rel(path), ctx->uid, ctx->gid, AT_SYMLINK_NOFOLLOW);
}

static void flatten(struct stat *st)
{
	st->st_atim = st->st_mtim;
	st->st_ctim = st->st_mtim;
}

// The calls below always go by path, even when handed a handle: the kernel passes one for an open
// folder too, and a folder's handle here is a DIR pointer, not a file descriptor. A file deleted while
// open still has a path, the .fuse_hidden name it was renamed to.
static int cf_getattr(const char *path, struct stat *st, struct fuse_file_info *fi)
{
	int rc = fstatat(g_root, rel(path), st, AT_SYMLINK_NOFOLLOW);
	(void)fi;
	if (rc != 0)
		return -errno;
	if (g_owner)
		report_owner(path, st);
	else
		flatten(st);
	return 0;
}

static int cf_access(const char *path, int mask)
{
	return faccessat(g_root, rel(path), mask, 0) != 0 ? -errno : 0;
}

static int cf_readlink(const char *path, char *buf, size_t size)
{
	ssize_t got = readlinkat(g_root, rel(path), buf, size - 1);
	if (got < 0)
		return -errno;
	buf[got] = '\0';
	return 0;
}

static int cf_opendir(const char *path, struct fuse_file_info *fi)
{
	int fd = openat(g_root, rel(path), O_RDONLY | O_DIRECTORY);
	if (fd < 0)
		return -errno;
	DIR *dir = fdopendir(fd);
	if (dir == NULL)
	{
		int err = errno;
		close(fd);
		return -err;
	}
	fi->fh = (uint64_t)(uintptr_t)dir;
	return 0;
}

static int cf_readdir(const char *path, void *buf, fuse_fill_dir_t filler, off_t offset, struct fuse_file_info *fi, enum fuse_readdir_flags flags)
{
	DIR           *dir = (DIR *)(uintptr_t)fi->fh;
	struct dirent *entry;
	(void)path; (void)flags;
	seekdir(dir, offset);
	errno = 0;
	while ((entry = readdir(dir)) != NULL)
	{
		struct stat st;
		// A file deleted while still open waits under this name until it is closed. Nobody asked for it.
		if (strncmp(entry->d_name, ".fuse_hidden", 12) == 0)
			continue;
		memset(&st, 0, sizeof(st));
		st.st_ino  = entry->d_ino;
		st.st_mode = (mode_t)entry->d_type << 12;
		if (filler(buf, entry->d_name, &st, telldir(dir), 0) != 0)
			break;
	}
	return errno != 0 ? -errno : 0;
}

static int cf_releasedir(const char *path, struct fuse_file_info *fi)
{
	(void)path;
	closedir((DIR *)(uintptr_t)fi->fh);
	return 0;
}

static int cf_mknod(const char *path, mode_t mode, dev_t rdev)
{
	if (mknodat(g_root, rel(path), mode, rdev) != 0)
		return -errno;
	own(path);
	stamp(path);
	stamp_parent(path);
	return 0;
}

static int cf_mkdir(const char *path, mode_t mode)
{
	if (mkdirat(g_root, rel(path), mode) != 0)
		return -errno;
	own(path);
	stamp(path);
	stamp_parent(path);
	return 0;
}

static int cf_unlink(const char *path)
{
	if (unlinkat(g_root, rel(path), 0) != 0)
		return -errno;
	stamp_parent(path);
	return 0;
}

static int cf_rmdir(const char *path)
{
	if (unlinkat(g_root, rel(path), AT_REMOVEDIR) != 0)
		return -errno;
	stamp_parent(path);
	return 0;
}

static int cf_symlink(const char *target, const char *path)
{
	if (symlinkat(target, g_root, rel(path)) != 0)
		return -errno;
	own(path);
	stamp(path);
	stamp_parent(path);
	return 0;
}

static int cf_rename(const char *from, const char *to, unsigned int flags)
{
	if (renameat2(g_root, rel(from), g_root, rel(to), flags) != 0)
		return -errno;
	stamp_parent(from);
	stamp_parent(to);
	return 0;
}

static int cf_link(const char *from, const char *to)
{
	if (g_owner && is_sealed(from))
		return -EPERM;
	if (linkat(g_root, rel(from), g_root, rel(to), 0) != 0)
		return -errno;
	stamp_parent(to);
	return 0;
}

static int cf_chmod(const char *path, mode_t mode, struct fuse_file_info *fi)
{
	int rc = fchmodat(g_root, rel(path), mode, 0);
	(void)fi;
	return rc != 0 ? -errno : 0;
}

static int cf_chown(const char *path, uid_t uid, gid_t gid, struct fuse_file_info *fi)
{
	int rc = fchownat(g_root, rel(path), uid, gid, AT_SYMLINK_NOFOLLOW);
	(void)fi;
	return rc != 0 ? -errno : 0;
}

static int cf_truncate(const char *path, off_t size, struct fuse_file_info *fi)
{
	int rc;
	if (fi != NULL)
	{
		rc = ftruncate((int)fi->fh, size);
	}
	else
	{
		int fd = openat(g_root, rel(path), O_WRONLY);
		if (fd < 0)
			return -errno;
		rc = ftruncate(fd, size);
		if (rc != 0)
		{
			int err = errno;
			close(fd);
			return -err;
		}
		close(fd);
	}
	if (rc != 0)
		return -errno;
	stamp(path);
	return 0;
}

static int cf_utimens(const char *path, const struct timespec tv[2], struct fuse_file_info *fi)
{
	struct timespec times[2];
	(void)fi;
	// UTIME_NOW is a plain `touch`: that means the clock here. A time the program chose stays.
	times[0] = tv[0];
	times[1] = tv[1];
	if (times[0].tv_nsec == UTIME_NOW) { times[0].tv_sec = now(); times[0].tv_nsec = 0; }
	if (times[1].tv_nsec == UTIME_NOW) { times[1].tv_sec = now(); times[1].tv_nsec = 0; }
	return utimensat(g_root, rel(path), times, AT_SYMLINK_NOFOLLOW) != 0 ? -errno : 0;
}

static int cf_create(const char *path, mode_t mode, struct fuse_file_info *fi)
{
	int fd = openat(g_root, rel(path), fi->flags, mode);
	if (fd < 0)
		return -errno;
	fi->fh = (uint64_t)fd;
	own(path);
	stamp(path);
	stamp_parent(path);
	return 0;
}

static int cf_open(const char *path, struct fuse_file_info *fi)
{
	int fd = openat(g_root, rel(path), fi->flags & ~O_NOFOLLOW);
	if (fd < 0)
		return -errno;
	fi->fh = (uint64_t)fd;
	if (fi->flags & O_TRUNC)
		stamp(path);
	return 0;
}

static int cf_read(const char *path, char *buf, size_t size, off_t offset, struct fuse_file_info *fi)
{
	ssize_t got = pread((int)fi->fh, buf, size, offset);
	(void)path;
	return got < 0 ? -errno : (int)got;
}

static int cf_write(const char *path, const char *buf, size_t size, off_t offset, struct fuse_file_info *fi)
{
	ssize_t put = pwrite((int)fi->fh, buf, size, offset);
	if (put < 0)
		return -errno;
	stamp(path);
	return (int)put;
}

static int cf_statfs(const char *path, struct statvfs *out)
{
	int fd = openat(g_root, rel(path), O_RDONLY | O_NOFOLLOW | O_PATH);
	int rc;
	if (fd < 0)
		return -errno;
	rc = fstatvfs(fd, out);
	close(fd);
	return rc != 0 ? -errno : 0;
}

static int cf_flush(const char *path, struct fuse_file_info *fi)
{
	(void)path; (void)fi;
	return 0;
}

static int cf_release(const char *path, struct fuse_file_info *fi)
{
	(void)path;
	close((int)fi->fh);
	return 0;
}

static int cf_fsync(const char *path, int datasync, struct fuse_file_info *fi)
{
	(void)path;
	return (datasync ? fdatasync((int)fi->fh) : fsync((int)fi->fh)) != 0 ? -errno : 0;
}

static off_t cf_lseek(const char *path, off_t off, int whence, struct fuse_file_info *fi)
{
	off_t at = lseek((int)fi->fh, off, whence);
	(void)path;
	return at < 0 ? -errno : at;
}

static int cf_fallocate(const char *path, int mode, off_t offset, off_t length, struct fuse_file_info *fi)
{
	if (fallocate((int)fi->fh, mode, offset, length) != 0)
		return -errno;
	stamp(path);
	return 0;
}

static void *cf_init(struct fuse_conn_info *conn, struct fuse_config *cfg)
{
	(void)conn;
	// The real file and this mount's answers must never disagree, so nothing is cached in the kernel,
	// and paths are always handed through rather than replaced by node ids.
	cfg->use_ino          = 1;
	cfg->entry_timeout    = 0;
	cfg->attr_timeout     = 0;
	cfg->negative_timeout = 0;
	cfg->direct_io        = 0;
	cfg->kernel_cache     = 0;
	cfg->auto_cache       = 0;

	// Owner mode reports real times, so the kernel may keep what it was told for a second: uncached, every
	// lookup is a round trip through here and onto a bind mount that is slow already. A file's contents are
	// reread when its time or size changes, as an edit from the host does. A folder sealed while its
	// entries are cached reads as the shell user's for that second, so the agent waits it out (Sandbox.cs).
	if (g_owner)
	{
		cfg->entry_timeout    = 1.0;
		cfg->attr_timeout     = 1.0;
		cfg->negative_timeout = 1.0;
		cfg->auto_cache       = 1;
	}
	return NULL;
}

static const struct fuse_operations kOps = {
	.init       = cf_init,
	.getattr    = cf_getattr,
	.access     = cf_access,
	.readlink   = cf_readlink,
	.opendir    = cf_opendir,
	.readdir    = cf_readdir,
	.releasedir = cf_releasedir,
	.mknod      = cf_mknod,
	.mkdir      = cf_mkdir,
	.unlink     = cf_unlink,
	.rmdir      = cf_rmdir,
	.symlink    = cf_symlink,
	.rename     = cf_rename,
	.link       = cf_link,
	.chmod      = cf_chmod,
	.chown      = cf_chown,
	.truncate   = cf_truncate,
	.utimens    = cf_utimens,
	.create     = cf_create,
	.open       = cf_open,
	.read       = cf_read,
	.write      = cf_write,
	.statfs     = cf_statfs,
	.flush      = cf_flush,
	.release    = cf_release,
	.fsync      = cf_fsync,
	.lseek      = cf_lseek,
	.fallocate  = cf_fallocate,
};

int main(int argc, char *argv[])
{
	char *folder;
	char *options;
	if (argc == 5 && strcmp(argv[1], "--owner") == 0)
	{
		g_owner = 1;
		g_uid   = (uid_t)strtoul(argv[2], NULL, 10);
		g_gid   = (gid_t)strtoul(argv[3], NULL, 10);
		folder  = argv[4];
		options = "allow_other,default_permissions,fsname=ownerfs,subtype=ownerfs";
	}
	else if (argc == 3)
	{
		folder  = argv[1];
		g_clock = open(argv[2], O_RDONLY);
		options = "allow_other,default_permissions,fsname=clockfs";
		if (g_clock < 0)
		{
			perror("clockfs");
			return 1;
		}
	}
	else
	{
		fprintf(stderr, "usage: clockfs <folder> <clock file>\n       clockfs --owner <uid> <gid> <folder>\n");
		return 2;
	}
	g_root = open(folder, O_RDONLY | O_DIRECTORY);
	if (g_root < 0)
	{
		perror("clockfs");
		return 1;
	}
	umask(0);

	char *args[] = { argv[0], folder, "-o", options, NULL };
	return fuse_main(4, args, &kOps, NULL);
}
