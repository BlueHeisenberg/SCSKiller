// scskiller-host: what SCSKiller asks of the host when it runs in a Flatpak, whose sandbox has a /proc of its own and
// can't start Steam's runtime. The app runs it through flatpak-spawn --host and talks to it over stdin and stdout. The
// sandbox is not trusted: every folder this checks against comes from the host itself (the home folder from passwd,
// Steam's folder and its libraryfolders.vdf, the compatibility tool folders, the Flatpak's data folder), only this user's
// processes are seen, and the one program it starts is a warm whose command line it builds from checked parts. These
// checks bound what the app asks for and don't contain a compromised sandbox: the permission flatpak-spawn --host needs
// (talking to org.freedesktop.Flatpak) lets the sandbox run any host command itself.
//
// Frames, both ways: a 4-byte little-endian length, then that many bytes of NUL-terminated fields. A request's first
// field names the operation, a reply's is "ok" or "err" (one more field: why). The first request is hello. A frame that
// can't be read is answered "err" and ends the helper (exit code 2); a refused request leaves it running.
//
//   hello 1                        -> ok 1
//   whoami <pid ns inode> <ns pid> -> ok <pid> <start>    the host's pid of the process with that pid in that namespace
//   snapshot                       -> ok {<pid> <ppid> <start> <wine 0|1> <name> <token>}...
//   image <pid>                    -> ok <path>           only under Steam's folder, a library, a tool folder or the data folder
//   argv <pid>                     -> ok <arg>...         Steam's reaper alone, its arguments before "--"
//   launch {<key> <value>}...      -> ok <handle> <unit>  keys: steam compatdata install proton runtime work exe token, gpu
//                                                         (at most once), env NAME=VALUE and arg (in order, any number)
//   status <handle>                -> ok running | ok exited <code>
//   signal <pid> KILL|STOP|CONT <token> -> ok             only a warm's process that carries that token
//
// start: clock ticks since boot (/proc/pid/stat's field 22). name: a Wine process's Windows exe name, else the exe's file
// name, else the kernel's name for it. token: SCSKILLER_WARM_JOB's value when it has a token's form, else empty: nothing
// else of a process's environment is read out. unit: the warm's systemd scope, empty when it runs in none.
#define _GNU_SOURCE
#include <ctype.h>
#include <dirent.h>
#include <errno.h>
#include <fcntl.h>
#include <limits.h>
#include <poll.h>
#include <pwd.h>
#include <signal.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <strings.h>
#include <sys/prctl.h>
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <time.h>
#include <unistd.h>

#ifndef SCSKILLER_APP_ID
#error "define SCSKILLER_APP_ID as the Flatpak's app id"
#endif
#ifndef SYS_pidfd_open
#define SYS_pidfd_open 434
#endif
#ifndef SYS_pidfd_send_signal
#define SYS_pidfd_send_signal 424
#endif
#ifndef SYS_close_range
#define SYS_close_range 436
#endif

#define PROTOCOL "1"
#define MAX_REQUEST (1u << 20)
#define MAX_FIELDS 8192
#define MAX_LIBS 64
#define MAX_ENV 256
#define TOKEN_VAR "SCSKILLER_WARM_JOB"
#define WARM_EXE "scskiller_warm.exe"
#define ENTRY_POINT "_v2-entry-point"
#define UNIT_PREFIX "scskiller-warm-"
#define ENTRY_FD "/proc/self/fd/3"

// the reply goes back unless the operation returned early through REFUSE
#define REFUSE(...) do { fail(__VA_ARGS__); return; } while (0)

typedef struct { char *p; size_t n, cap; } Buf;

static void put(Buf *b, const void *s, size_t n)
{
    if (n == 0) return;
    if (b->n + n > b->cap) {
        size_t cap = b->cap ? b->cap : 4096;
        while (cap < b->n + n) cap *= 2;
        char *p = realloc(b->p, cap);
        if (!p) _exit(3);
        b->p = p;
        b->cap = cap;
    }
    memcpy(b->p + b->n, s, n);
    b->n += n;
}

static void field(Buf *b, const char *s) { put(b, s, strlen(s) + 1); }

static void number_field(Buf *b, long long v)
{
    char s[24];
    snprintf(s, sizeof s, "%lld", v);
    field(b, s);
}

static Buf out;

static bool write_all(int fd, const char *p, size_t n)
{
    while (n > 0) {
        ssize_t w = write(fd, p, n);
        if (w < 0 && errno == EINTR) continue;
        if (w <= 0) return false;
        p += w;
        n -= (size_t)w;
    }
    return true;
}

// fewer than n at the end of the stream
static size_t read_all(int fd, char *p, size_t n)
{
    size_t got = 0;
    while (got < n) {
        ssize_t r = read(fd, p + got, n - got);
        if (r < 0 && errno == EINTR) continue;
        if (r <= 0) break;
        got += (size_t)r;
    }
    return got;
}

static void reply(void)
{
    unsigned char len[4] = {(unsigned char)out.n, (unsigned char)(out.n >> 8), (unsigned char)(out.n >> 16), (unsigned char)(out.n >> 24)};
    if (!write_all(1, (const char *)len, 4) || !write_all(1, out.p, out.n)) exit(0);   // the app is gone
}

static void ok(void)
{
    out.n = 0;
    field(&out, "ok");
}

static void fail(const char *fmt, ...)
{
    char why[512];
    va_list ap;
    va_start(ap, fmt);
    vsnprintf(why, sizeof why, fmt, ap);
    va_end(ap);
    out.n = 0;
    field(&out, "err");
    field(&out, why);
}

static _Noreturn void malformed(void)
{
    fail("malformed frame");
    reply();
    exit(2);
}

static bool digits(const char *s, size_t max)
{
    size_t n = strlen(s);
    if (n == 0 || n > max) return false;
    for (; *s; s++)
        if (!isdigit((unsigned char)*s)) return false;
    return true;
}

static bool number(const char *s, long long max, long long *v)
{
    if (!digits(s, 19)) return false;
    errno = 0;
    long long n = strtoll(s, NULL, 10);
    if (errno || n > max) return false;
    *v = n;
    return true;
}

static bool hex(const char *s, size_t n)
{
    for (size_t i = 0; i < n; i++)
        if (!isdigit((unsigned char)s[i]) && !(s[i] >= 'a' && s[i] <= 'f')) return false;
    return true;
}

// <pid>.<start>, and for a run's token .<32 lowercase hex> after it: what WarmJob puts in a warm's environment
static bool token_form(const char *t, bool run)
{
    size_t pid = strspn(t, "0123456789");
    if (pid == 0 || pid > 10 || t[pid] != '.') return false;
    const char *start = t + pid + 1;
    size_t ticks = strspn(start, "0123456789");
    if (ticks == 0 || ticks > 20) return false;
    const char *rest = start + ticks;
    if (*rest == 0) return !run;
    return *rest == '.' && strlen(rest + 1) == 32 && hex(rest + 1, 32);
}

static bool printable(const char *s, size_t max)
{
    size_t n = strlen(s);
    if (n == 0 || n > max) return false;
    for (; *s; s++)
        if ((unsigned char)*s < 0x20 || *s == 0x7f) return false;
    return true;
}

static bool charset(const char *s, size_t max, const char *extra)
{
    size_t n = strlen(s);
    if (n == 0 || n > max) return false;
    for (; *s; s++)
        if (!isalnum((unsigned char)*s) && !strchr(extra, *s)) return false;
    return true;
}

// i,j,...: scskiller_warm's item lists
static bool number_list(const char *s)
{
    if (strlen(s) > (1u << 16)) return false;
    for (;;) {
        size_t n = strspn(s, "0123456789");
        if (n == 0 || n > 19) return false;
        s += n;
        if (*s == 0) return true;
        if (*s++ != ',') return false;
    }
}

static const char *base(const char *p)
{
    const char *s = strrchr(p, '/');
    return s ? s + 1 : p;
}

// strictly below root
static bool under(const char *path, const char *root)
{
    size_t n = strlen(root);
    if (n == 0) return false;
    if (n == 1) return path[0] == '/' && path[1] != 0;
    return strncmp(path, root, n) == 0 && path[n] == '/' && path[n + 1] != 0;
}

// path's part below root, which it is under
static const char *below(const char *path, const char *root) { return path + (strlen(root) == 1 ? 1 : strlen(root) + 1); }

// a file right in dir
static bool in_folder(const char *path, const char *dir) { return under(path, dir) && !strchr(below(path, dir), '/'); }

static bool join(char *dst, const char *a, const char *b)
{
    size_t n = strlen(a);
    int w = snprintf(dst, PATH_MAX, "%s%s%s", a, n && a[n - 1] == '/' ? "" : "/", b);
    return w > 0 && w < PATH_MAX;
}

// never with a backslash, as given or resolved: Wine and vkd3d-proton take it for a separator in a Windows path
static bool resolve(const char *path, char *real)
{
    return path[0] == '/' && strlen(path) < PATH_MAX && !strchr(path, '\\') && realpath(path, real) && !strchr(real, '\\');
}

// a path part Wine opens as written: it strips a trailing dot or space (so folds "." and ".."), ':' starts a stream and
// a backslash separates
static bool wine_part(const char *part, size_t n)
{
    return n && part[n - 1] != '.' && part[n - 1] != ' ' && !memchr(part, ':', n) && !memchr(part, '\\', n);
}

// entry may be name to Wine, which matches names ignoring case: a character beyond ASCII is taken to match any one, as
// 'ſ' does 'S'
static bool alike(const char *entry, const char *name)
{
    const unsigned char *e = (const unsigned char *)entry, *n = (const unsigned char *)name;
    for (; *e && *n; n++)
        if (*e < 0x80) {
            if (tolower(*e) != tolower(*n)) return false;
            e++;
        } else
            for (e++; (*e & 0xc0) == 0x80; e++) {}
    return !*e && !*n;
}

// name, which isn't in dir, is what Wine opens there: no entry it would take for it ignoring case, no '~' that would
// make it the 8.3 name of a long one, and no entry with a '?', as Wine keeps a reparse point (no name of ours has one)
static bool unmatched(const char *dir, const char *name)
{
    for (const char *c = name; *c; c++)
        if (*c == '~' || (unsigned char)*c >= 0x7f) return false;
    DIR *d = opendir(dir);
    if (!d) return false;
    bool clear = true;
    for (struct dirent *de; clear && (de = readdir(d));) clear = !strchr(de->d_name, '?') && !alike(de->d_name, name);
    closedir(d);
    return clear;
}

// a file that may not exist yet: its folder resolved, its name as given, each part one Wine opens as written. An entry
// there that doesn't resolve (a dangling link) is refused: the warm would write through it.
static bool resolve_new(const char *path, char *real)
{
    for (const char *p = path; *p; p += *p == '/') {
        size_t n = strcspn(p, "/");
        if (n && !wine_part(p, n)) return false;
        p += n;
    }
    if (resolve(path, real)) return true;
    struct stat st;
    if (path[0] != '/' || strlen(path) >= PATH_MAX || lstat(path, &st) == 0 || errno != ENOENT) return false;
    char dir[PATH_MAX], rdir[PATH_MAX];
    strcpy(dir, path);
    char *slash = strrchr(dir, '/');
    const char *name = path + (slash - dir) + 1;
    if (!*name) return false;
    *slash = 0;
    return resolve(dir[0] ? dir : "/", rdir) && unmatched(rdir, name) && join(real, rdir, name);
}

static uid_t uid;
static char user[256], home[PATH_MAX], steam[PATH_MAX], data[PATH_MAX], tools[3][PATH_MAX];
static struct { char spelled[PATH_MAX], real[PATH_MAX]; } libs[MAX_LIBS];
static int nlibs;

static bool find_home(void)
{
    struct passwd *pw = getpwuid(uid);
    if (pw && pw->pw_name && strlen(pw->pw_name) < sizeof user) strcpy(user, pw->pw_name);
#ifdef SCSKILLER_HOST_TEST
    const char *h = getenv("SCSKILLER_HOST_HOME");
#else
    // never HOME: flatpak-spawn starts this with an environment the sandbox chooses
    const char *h = pw ? pw->pw_dir : NULL;
#endif
    return h && resolve(h, home);
}

// libraryfolders.vdf's "path" values (Steam's folder when it lists none), as SteamSource.Libraries reads them
static void read_libraries(const char *vdf)
{
    Buf b = {0};
    // never a FIFO, whose open and read would wait for a writer
    int fd = open(vdf, O_RDONLY | O_NONBLOCK | O_CLOEXEC);
    struct stat st;
    if (fd >= 0 && (fstat(fd, &st) != 0 || !S_ISREG(st.st_mode))) {
        close(fd);
        fd = -1;
    }
    if (fd >= 0) {
        char chunk[65536];
        ssize_t r;
        while (b.n < (1u << 20) && (r = read(fd, chunk, sizeof chunk)) > 0) put(&b, chunk, (size_t)r);
        close(fd);
    }
    put(&b, "", 1);
    bool after_key = false;
    for (char *p = b.p; *p && nlibs < MAX_LIBS;) {
        if (isspace((unsigned char)*p)) p++;
        else if (p[0] == '/' && p[1] == '/') p += strcspn(p, "\n");
        else if (*p != '"') {
            after_key = false;
            p++;
        } else {
            char value[PATH_MAX];
            size_t n = 0;
            bool fits = true;
            for (p++; *p && *p != '"'; p++) {
                if (*p == '\\' && p[1]) p++;
                if (n + 1 < sizeof value) value[n++] = *p;
                else fits = false;
            }
            if (*p) p++;
            value[n] = 0;
            if (!after_key) {
                after_key = !strcasecmp(value, "path");
                continue;
            }
            after_key = false;
            bool skip = !fits || value[0] != '/';
            for (int i = 0; i < nlibs && !skip; i++) skip = !strcasecmp(libs[i].spelled, value);
            if (skip) continue;
            strcpy(libs[nlibs].spelled, value);
            if (!realpath(value, libs[nlibs].real)) libs[nlibs].real[0] = 0;
            nlibs++;
        }
    }
    free(b.p);
    if (nlibs == 0 && steam[0]) {
        strcpy(libs[0].spelled, steam);
        strcpy(libs[0].real, steam);
        nlibs = 1;
    }
}

// again on each request that needs them: Steam and the app make their folders while this runs
static void find_roots(void)
{
    char p[PATH_MAX];
    if (!(join(p, home, ".steam/root") && realpath(p, steam)) && !(join(p, home, ".local/share/Steam") && realpath(p, steam))) steam[0] = 0;
    if (!join(p, home, ".var/app/" SCSKILLER_APP_ID "/data/SCSKiller") || !realpath(p, data)) data[0] = 0;
    if (!steam[0] || !join(p, steam, "compatibilitytools.d") || !realpath(p, tools[0])) tools[0][0] = 0;
    if (!realpath("/usr/share/steam/compatibilitytools.d", tools[1])) tools[1][0] = 0;
    if (!realpath("/usr/local/share/steam/compatibilitytools.d", tools[2])) tools[2][0] = 0;
    nlibs = 0;
    if (steam[0] && join(p, steam, "steamapps/libraryfolders.vdf")) read_libraries(p);
}

// a library's steamapps/<sub>, its links resolved
static bool library_folder(int lib, const char *sub, char *real)
{
    char p[PATH_MAX];
    return libs[lib].real[0] && snprintf(p, sizeof p, "%s/steamapps/%s", libs[lib].real, sub) < (int)sizeof p && realpath(p, real);
}

// the library whose steamapps/<sub> holds path, and path's part below that; -1 for none
static int library_of(const char *path, const char *sub, const char **rest)
{
    char dir[PATH_MAX];
    for (int i = 0; i < nlibs; i++)
        if (library_folder(i, sub, dir) && under(path, dir)) {
            *rest = below(path, dir);
            return i;
        }
    return -1;
}

static bool allowed_image(const char *path)
{
    if (under(path, steam) || under(path, data)) return true;
    for (int i = 0; i < 3; i++)
        if (under(path, tools[i])) return true;
    for (int i = 0; i < nlibs; i++)
        if (under(path, libs[i].real)) return true;
    return false;
}

static int proc = -1, devnull = -1;

static int open_pid(long long pid)
{
    char s[24];
    snprintf(s, sizeof s, "%lld", pid);
    return openat(proc, s, O_RDONLY | O_DIRECTORY | O_CLOEXEC);
}

// /proc/<pid> is owned by the process's user, by root for a process that can't be read
static bool ours(int dfd)
{
    struct stat st;
    return fstat(dfd, &st) == 0 && st.st_uid == uid;
}

static bool slurp(int dfd, const char *name, Buf *b, size_t max)
{
    b->n = 0;
    int fd = openat(dfd, name, O_RDONLY | O_CLOEXEC);
    if (fd < 0) return false;
    char chunk[65536];
    ssize_t r;
    while (b->n < max && (r = read(fd, chunk, sizeof chunk)) > 0) put(b, chunk, (size_t)r);
    close(fd);
    put(b, "", 1);
    b->n--;
    return true;
}

typedef struct { char name[64], state; long long ppid, start; } Stat;

static bool read_stat(int dfd, Stat *s)
{
    static Buf b;
    if (!slurp(dfd, "stat", &b, 4096)) return false;
    // pid (name) state ppid ... starttime (field 22): the name may hold spaces and parentheses
    char *open = strchr(b.p, '('), *close = strrchr(b.p, ')');
    if (!open || !close || close < open) return false;
    snprintf(s->name, sizeof s->name, "%.*s", (int)(close - open - 1), open + 1);
    s->state = close[1] ? close[2] : 0;
    int i = 0;
    bool ppid = false, start = false;
    for (char *f = strtok(close + 1, " \n"); f; f = strtok(NULL, " \n"), i++)
        if (i == 1) ppid = number(f, INT_MAX, &s->ppid);
        else if (i == 19) start = number(f, LLONG_MAX, &s->start);
    return ppid && start;
}

static bool read_exe(int dfd, char *exe)
{
    ssize_t n = readlinkat(dfd, "exe", exe, PATH_MAX - 1);
    if (n <= 0) return false;
    exe[n] = 0;
    if (n > 10 && !strcmp(exe + n - 10, " (deleted)")) exe[n - 10] = 0;
    return true;
}

// Wine runs every Windows process as its loader
static bool is_wine(const char *exe)
{
    const char *b = base(exe);
    return !strcmp(b, "wine") || !strcmp(b, "wine64") || !strcmp(b, "wine-preloader") || !strcmp(b, "wine64-preloader");
}

// the exe file name a Wine process's command line starts with (Wine sets argv to the Windows command line), with the
// .exe a Windows launch adds when it's left out
static bool windows_name(int dfd, char *name, size_t cap)
{
    static Buf b;
    if (!slurp(dfd, "cmdline", &b, 65536)) return false;
    const char *start = b.p;
    for (const char *c = b.p; *c; c++)
        if (*c == '\\' || *c == '/') start = c + 1;
    size_t n = strlen(start);
    if (n == 0 || n + 5 > cap) return false;
    memcpy(name, start, n + 1);
    if (!strchr(name, '.')) strcat(name, ".exe");
    return true;
}

// a Wine process maps its exe from the Unix path Wine opened, whatever drive its command line names; read a chunk at
// a time, since a game's maps can run to millions of lines
static bool wine_image(int dfd, const char *name, char *image)
{
    int fd = openat(dfd, "maps", O_RDONLY | O_CLOEXEC);
    if (fd < 0) return false;
    Buf line = {0};
    char chunk[65536];
    bool found = false, end = false;
    while (!found && !end) {
        ssize_t r = read(fd, chunk, sizeof chunk);
        if (r < 0 && errno == EINTR) continue;
        end = r <= 0;
        size_t n = end ? 0 : (size_t)r, from = 0;
        while (!found) {
            char *nl = from < n ? memchr(chunk + from, '\n', n - from) : NULL;
            size_t take = nl ? (size_t)(nl - chunk) - from : n - from;
            put(&line, chunk + from, take);
            from += take + (nl != NULL);
            if (!nl && !(end && line.n > 0)) break;   // the rest of the line comes with the next read
            put(&line, "", 1);
            char *slash = strchr(line.p, '/');
            if (slash && !strcasecmp(base(slash), name) && strlen(slash) < PATH_MAX) {
                strcpy(image, slash);
                found = true;
            }
            line.n = 0;
        }
    }
    free(line.p);
    close(fd);
    return found;
}

// a Wine process's Windows exe as it was mapped, else the process's executable
static bool image_path(int dfd, char *image)
{
    char exe[PATH_MAX], name[NAME_MAX + 8];
    if (!read_exe(dfd, exe)) return false;
    if (!is_wine(exe)) {
        strcpy(image, exe);
        return true;
    }
    return windows_name(dfd, name, sizeof name) && wine_image(dfd, name, image);
}

// SCSKILLER_WARM_JOB's value in the environment the process started with, when it has a token's form
static bool read_token(int dfd, char *token, size_t cap)
{
    static Buf b;
    if (!slurp(dfd, "environ", &b, 4u << 20)) return false;
    size_t len = strlen(TOKEN_VAR);
    for (size_t at = 0; at < b.n; at += strlen(b.p + at) + 1)
        if (!strncmp(b.p + at, TOKEN_VAR "=", len + 1)) {
            const char *v = b.p + at + len + 1;
            if (!token_form(v, false) || strlen(v) >= cap) return false;
            strcpy(token, v);
            return true;
        }
    return false;
}

static void op_whoami(char **f)
{
    long long inode, nspid;
    if (!number(f[1], LLONG_MAX, &inode) || !number(f[2], INT_MAX, &nspid)) REFUSE("bad request");
    char want[64];
    snprintf(want, sizeof want, "pid:[%lld]", inode);
    DIR *d = opendir("/proc");
    if (!d) REFUSE("no /proc");
    static Buf status;
    struct dirent *e;
    while ((e = readdir(d))) {
        long long pid;
        if (!number(e->d_name, INT_MAX, &pid)) continue;
        int dfd = open_pid(pid);
        if (dfd < 0) continue;
        char ns[64];
        ssize_t n = ours(dfd) ? readlinkat(dfd, "ns/pid", ns, sizeof ns - 1) : -1;
        if (n > 0) ns[n] = 0;
        bool match = false;
        Stat st;
        if (n > 0 && !strcmp(ns, want) && slurp(dfd, "status", &status, 65536)) {
            // NSpid: its pid in each namespace from the host's down, its own last
            char *line = strstr(status.p, "\nNSpid:"), *last = NULL;
            if (line) {
                line[strcspn(line + 1, "\n") + 1] = 0;
                for (char *t = strtok(line + 7, " \t"); t; t = strtok(NULL, " \t")) last = t;
            }
            long long v;
            match = last && number(last, INT_MAX, &v) && v == nspid && read_stat(dfd, &st);
        }
        close(dfd);
        if (match) {
            closedir(d);
            ok();
            number_field(&out, pid);
            number_field(&out, st.start);
            return;
        }
    }
    closedir(d);
    fail("not found");
}

static void op_snapshot(void)
{
    DIR *d = opendir("/proc");
    if (!d) REFUSE("no /proc");
    ok();
    struct dirent *e;
    while ((e = readdir(d))) {
        long long pid;
        if (!number(e->d_name, INT_MAX, &pid)) continue;
        int dfd = open_pid(pid);
        if (dfd < 0) continue;
        Stat st;
        if (ours(dfd) && read_stat(dfd, &st)) {
            char exe[PATH_MAX], name[NAME_MAX + 8], token[96] = "";
            bool linked = read_exe(dfd, exe), wine = linked && is_wine(exe);
            const char *shown = !linked ? st.name : !wine ? base(exe) : windows_name(dfd, name, sizeof name) ? name : st.name;
            if (wine && !read_token(dfd, token, sizeof token)) token[0] = 0;
            number_field(&out, pid);
            number_field(&out, st.ppid);
            number_field(&out, st.start);
            field(&out, wine ? "1" : "0");
            field(&out, shown);
            field(&out, token);
        }
        close(dfd);
    }
    closedir(d);
}

static void op_image(char **f)
{
    long long pid;
    if (!number(f[1], INT_MAX, &pid)) REFUSE("bad request");
    find_roots();
    int dfd = open_pid(pid);
    char image[PATH_MAX];
    bool got = dfd >= 0 && ours(dfd) && image_path(dfd, image);
    if (dfd >= 0) close(dfd);
    if (!got) REFUSE("no image");
    if (!allowed_image(image)) REFUSE("not allowed");
    ok();
    field(&out, image);
}

static void op_argv(char **f)
{
    long long pid;
    if (!number(f[1], INT_MAX, &pid)) REFUSE("bad request");
    find_roots();
    static Buf b;
    char reaper[PATH_MAX], exe[PATH_MAX];
    int dfd = open_pid(pid);
    bool is_reaper = dfd >= 0 && ours(dfd) && steam[0] && join(reaper, steam, "ubuntu12_32/reaper") && read_exe(dfd, exe) && !strcmp(exe, reaper);
    bool got = is_reaper && slurp(dfd, "cmdline", &b, 1u << 20);
    if (dfd >= 0) close(dfd);
    if (!is_reaper) REFUSE("not allowed");
    if (!got) REFUSE("no arguments");
    ok();
    // what follows "--" is the launch's own command line
    for (size_t at = 0; at < b.n && strcmp(b.p + at, "--"); at += strlen(b.p + at) + 1) field(&out, b.p + at);
}

typedef struct { pid_t pid; bool scoped, exited; int code; char token[96]; } Handle;
static Handle *handles;
static int nhandles;

// a Windows path as the warm opens it in the prefix, "X:\..." through the prefix's dosdevices or \\?\unix\..., as the
// host path it names; each part one Wine opens as written, never one it folds before it follows a link
static bool win_to_host(const char *prefix, const char *win, char *host)
{
    if (!printable(win, 32767) || strchr(win, '/')) return false;
    const char *p = strncmp(win, "\\\\?\\", 4) ? win : win + 4;
    char root[PATH_MAX], rel[PATH_MAX];
    const char *rest;
    if (!strncasecmp(p, "unix\\", 5)) {
        strcpy(root, "/");
        rest = p + 5;
    } else if (isalpha((unsigned char)p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == 0)) {
        char link[PATH_MAX];
        snprintf(link, sizeof link, "%s/dosdevices/%c:", prefix, tolower((unsigned char)p[0]));
        if (!realpath(link, root)) return false;
        rest = p[2] ? p + 3 : p + 2;
    } else return false;
    if (strlen(rest) >= sizeof rel) return false;
    strcpy(rel, rest);
    if (!*rel) {
        strcpy(host, root);
        return true;
    }
    for (char *part = rel;;) {
        char *next = strchr(part, '\\');
        if (next) *next = 0;
        if (!wine_part(part, strlen(part))) return false;
        if (!next) break;
        *next = '/';
        part = next + 1;
    }
    return join(host, root, rel);
}

// AppStore's work folders: games/<id>/work or games/<id>/work-<n>
static bool work_name(const char *name) { return !strcmp(name, "work") || (!strncmp(name, "work-", 5) && digits(name + 5, 10)); }

// path's parts below the data folder's sub, split at each '/' into parts (at most max); 0 when it isn't below it
static int data_parts(const char *path, const char *sub, char *copy, char **parts, int max)
{
    char dir[PATH_MAX];
    if (!data[0] || !join(dir, data, sub) || !under(path, dir)) return 0;
    strcpy(copy, below(path, dir));
    int n = 0;
    for (char *part = copy; part && n < max; n++) {
        parts[n] = part;
        part = strchr(part, '/');
        if (part) *part++ = 0;
    }
    return n;
}

static bool is_work(const char *real)
{
    char copy[PATH_MAX], *parts[3];
    return data_parts(real, "games", copy, parts, 3) == 2 && work_name(parts[1]);
}

// ProtonWarmHost's copies: native/<key>/scskiller_warm.exe and native/<key>/segheap/scskiller_warm.exe
static bool is_native_warm(const char *real)
{
    char copy[PATH_MAX], *parts[4];
    int n = data_parts(real, "native", copy, parts, 4);
    return (n == 2 && !strcmp(parts[1], WARM_EXE)) || (n == 3 && !strcmp(parts[1], "segheap") && !strcmp(parts[2], WARM_EXE));
}

// a staged child's image: games/<id>/<work folder>/stage-*/...
static bool is_staged(const char *real)
{
    char copy[PATH_MAX], *parts[4];
    return data_parts(real, "games", copy, parts, 4) == 4 && work_name(parts[1]) && !strncmp(parts[2], "stage-", 6);
}

// this user's or root's, and nobody else may write it: a file the launch runs, held from its check on; -1 for another.
// O_PATH opens nothing for reading, so a FIFO there can't block.
static int safe_file(const char *real, bool exec)
{
    int fd = open(real, O_PATH | O_NOFOLLOW | O_CLOEXEC);
    struct stat st;
    if (fd >= 0 && fstat(fd, &st) == 0 && S_ISREG(st.st_mode) && (st.st_uid == uid || st.st_uid == 0)
        && !(st.st_mode & (S_IWGRP | S_IWOTH)) && (!exec || (st.st_mode & S_IXUSR)))
        return fd;
    if (fd >= 0) close(fd);
    return -1;
}

// path opens the file fd holds
static bool still(int fd, const char *path)
{
    struct stat held, now;
    return fstat(fd, &held) == 0 && stat(path, &now) == 0 && held.st_dev == now.st_dev && held.st_ino == now.st_ino;
}

typedef struct { char **v; int n, cap; } Env;

static void env_set(Env *e, const char *name, const char *value)
{
    size_t len = strlen(name);
    char *kv = malloc(len + strlen(value) + 2);
    if (!kv) _exit(3);
    sprintf(kv, "%s=%s", name, value);
    for (int i = 0; i < e->n; i++)
        if (!strncmp(e->v[i], name, len) && e->v[i][len] == '=') {
            free(e->v[i]);
            e->v[i] = kv;
            return;
        }
    if (e->n + 2 > e->cap) {
        e->cap = e->cap ? e->cap * 2 : 64;
        e->v = realloc(e->v, (size_t)e->cap * sizeof *e->v);
        if (!e->v) _exit(3);
    }
    e->v[e->n++] = kv;
    e->v[e->n] = NULL;
}

static void env_free(Env *e)
{
    for (int i = 0; i < e->n; i++) free(e->v[i]);
    free(e->v);
}

// what the launch keeps of the host session this runs in: the home folder, the display, the locale, the user manager
static void env_base(Env *e)
{
    env_set(e, "HOME", home);
    if (user[0]) {
        env_set(e, "USER", user);
        env_set(e, "LOGNAME", user);
    }
    env_set(e, "PATH", "/usr/local/bin:/usr/bin:/bin");
    char rt[64], bus[96];
    snprintf(rt, sizeof rt, "/run/user/%u", (unsigned)uid);
    struct stat st;
    if (stat(rt, &st) == 0 && S_ISDIR(st.st_mode) && st.st_uid == uid && !(st.st_mode & 077)) {
        env_set(e, "XDG_RUNTIME_DIR", rt);
        snprintf(bus, sizeof bus, "%s/bus", rt);
        if (stat(bus, &st) == 0 && S_ISSOCK(st.st_mode) && st.st_uid == uid) {
            snprintf(bus, sizeof bus, "unix:path=%s/bus", rt);
            env_set(e, "DBUS_SESSION_BUS_ADDRESS", bus);
        }
    }
    static const struct { const char *name, *extra; } kept[] = {
        {"DISPLAY", ":._-/"}, {"WAYLAND_DISPLAY", "._-/"}, {"LANG", "_.@:-"}, {"LC_ALL", "_.@:-"}, {"LANGUAGE", "_.@:-"},
    };
    for (size_t i = 0; i < sizeof kept / sizeof *kept; i++) {
        const char *v = getenv(kept[i].name);
        if (v && charset(v, 256, kept[i].extra)) env_set(e, kept[i].name, v);
    }
    const char *xauth = getenv("XAUTHORITY");
    if (xauth && xauth[0] == '/' && printable(xauth, PATH_MAX - 1)) env_set(e, "XAUTHORITY", xauth);
}

// the driver-cache variables Core sets (Wine.CacheEnvironment's, a Vulkan warm's game cache), each with its value's
// check; a path comes back resolved in value
static bool cache_variable(const char *kv, char *value, char *why, size_t cap)
{
    const char *eq = strchr(kv, '=');
    if (!eq || eq == kv || eq - kv > 64) {
        snprintf(why, cap, "env: not NAME=VALUE");
        return false;
    }
    char name[72];
    snprintf(name, sizeof name, "%.*s", (int)(eq - kv), kv);
    const char *v = eq + 1, *rest;
    bool path = !strcmp(name, "__GL_SHADER_DISK_CACHE_PATH") || !strcmp(name, "VKD3D_SHADER_CACHE_PATH") || !strcmp(name, "AMD_VK_PIPELINE_CACHE_PATH"), good;
    if (strlen(v) >= PATH_MAX) good = false;
    else if (path)
        good = resolve_new(v, value) && (under(value, data) || library_of(value, "shadercache", &rest) >= 0);
    else if (!strcmp(name, "__GL_SHADER_DISK_CACHE_SIZE") || !strcmp(name, "__GL_SHADER_DISK_CACHE_SKIP_CLEANUP")
             || !strcmp(name, "__GL_SHADER_DISK_CACHE") || !strcmp(name, "AMD_VK_USE_PIPELINE_CACHE"))
        good = digits(v, 20);
    else if (!strcmp(name, "__GL_SHADER_DISK_CACHE_APP_NAME") || !strcmp(name, "AMD_VK_PIPELINE_CACHE_FILENAME"))
        good = charset(v, 128, "_.-");
    else if (!strcmp(name, "__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"))
        good = charset(v, 1024, "_.;-");
    else {
        snprintf(why, cap, "env: %s is not allowed", name);
        return false;
    }
    if (!good) snprintf(why, cap, "env: %s's value is not allowed", name);
    else if (!path) strcpy(value, v);
    return good;
}

// AftermathInit.Arg: <version>,<features>,<apis>,<dump flags>, each a 32-bit number in lowercase hex
static bool aftermath_init(const char *v)
{
    for (int part = 0; part < 4; part++) {
        size_t n = strspn(v, "0123456789abcdef");
        if (n == 0 || n > 8) return false;
        v += n;
        if (part < 3 && *v++ != ',') return false;
    }
    return *v == 0;
}

// scskiller_warm's options as Core passes them on Linux, each with its value's check
static bool warm_args(char **args, int n, const char *prefix, const char *work, const char *install, char *why, size_t cap)
{
    char host[PATH_MAX], real[PATH_MAX];
    if (n < 2 || !win_to_host(prefix, args[0], host) || !resolve(host, real) || strcmp(real, work)) {
        snprintf(why, cap, "arg: the work folder doesn't come first");
        return false;
    }
    if (!printable(args[1], 255) || strpbrk(args[1], "/\\:") || !strcmp(args[1], ".") || !strcmp(args[1], "..")) {
        snprintf(why, cap, "arg: the game's exe name is not a file name");
        return false;
    }
    for (int i = 2; i < n; i++) {
        const char *k = args[i];
        if (!strcmp(k, "--vulkan")) continue;
        if (i + 1 >= n) {
            snprintf(why, cap, "arg: %.64s has no value", k);
            return false;
        }
        const char *v = args[++i];
        long long num;
        bool good;
        if (!strcmp(k, "--threads") || !strcmp(k, "--start") || !strcmp(k, "--memory-mb") || !strcmp(k, "--rt-threads") || !strcmp(k, "--pass")
            || !strcmp(k, "--alive-turns"))
            good = number(v, LLONG_MAX, &num);
        else if (!strcmp(k, "--priority"))
            good = !strcmp(v, "idle") || !strcmp(v, "below");
        else if (!strcmp(k, "--skip") || !strcmp(k, "--isolate"))
            good = number_list(v);
        else if (!strcmp(k, "--out") || !strcmp(k, "--err") || !strcmp(k, "--stop-file") || !strcmp(k, "--alive-file") || (!strcmp(k, "--skip-keys") && v[0] == '@'))
            good = win_to_host(prefix, v + (v[0] == '@'), host) && resolve_new(host, real) && in_folder(real, work);
        else if (!strcmp(k, "--skip-keys")) {
            // <sha1 hex>,...
            size_t len = strlen(v);
            good = len % 41 == 40 && len < (1u << 20);
            for (size_t at = 0; good && at < len; at += 41) good = hex(v + at, 40) && (at + 40 == len || v[at + 40] == ',');
        } else if (!strcmp(k, "--layer"))
            good = win_to_host(prefix, v, host) && resolve(host, real) && under(real, work);
        else if (!strcmp(k, "--d3d12") || !strcmp(k, "--d3d11") || !strcmp(k, "--aftermath"))
            good = win_to_host(prefix, v, host) && resolve(host, real) && under(real, install);
        else if (!strcmp(k, "--ags"))
            good = win_to_host(prefix, v, host) && resolve(host, real) && (under(real, install) || under(real, data));
        else if (!strcmp(k, "--stage-path")) {
            // <install folder>\<dir>\<exe>, below the warm's staging folder
            char rel[300];
            good = printable(v, 260) && !strpbrk(v, "/:") && snprintf(rel, sizeof rel, "%s", v) > 0;
            for (char *part = rel, *next; good; part = next + 1) {
                next = strchr(part, '\\');
                if (next) *next = 0;
                good = wine_part(part, strlen(part));
                if (!next) break;
            }
        } else if (!strcmp(k, "--ags-app") || !strcmp(k, "--ags-engine"))
            good = printable(v, 256);
        else if (!strcmp(k, "--aftermath-init"))
            good = aftermath_init(v);
        else {
            snprintf(why, cap, "arg: %.64s is not allowed", k);
            return false;
        }
        if (!good) {
            snprintf(why, cap, "arg: %.64s's value is not allowed", k);
            return false;
        }
    }
    return true;
}

static const char *systemd_run(void)
{
#ifdef SCSKILLER_HOST_TEST
    const char *fake = getenv("SCSKILLER_HOST_SYSTEMD_RUN");
    return fake && fake[0] ? fake : NULL;
#else
    // systemd-run --user reaches the user manager through its private socket
    char sock[64];
    snprintf(sock, sizeof sock, "/run/user/%u/systemd/private", (unsigned)uid);
    struct stat st;
    if (stat(sock, &st) != 0 || !S_ISSOCK(st.st_mode)) return NULL;
    static const char *const paths[] = {"/usr/bin/systemd-run", "/bin/systemd-run"};
    for (size_t i = 0; i < sizeof paths / sizeof *paths; i++)
        if (stat(paths[i], &st) == 0 && S_ISREG(st.st_mode) && st.st_uid == 0 && !(st.st_mode & (S_IWGRP | S_IWOTH))) return paths[i];
    return NULL;
#endif
}

// the version systemd-run --version prints first ("systemd 256 (...)"); 0 when it doesn't say within 5 s
static int systemd_version(const char *run, char **envp)
{
    static int known = -1;
    if (known >= 0) return known;
    known = 0;
    int p[2];
    if (pipe2(p, O_CLOEXEC) != 0) return 0;
    pid_t pid = fork();
    if (pid == 0) {
        dup2(devnull, 0);
        dup2(p[1], 1);
        dup2(devnull, 2);
        char *argv[] = {(char *)run, "--version", NULL};
        execve(run, argv, envp);
        _exit(127);
    }
    close(p[1]);
    char text[256];
    size_t n = 0;
    struct timespec t0, t;
    clock_gettime(CLOCK_MONOTONIC, &t0);
    while (pid > 0 && n < sizeof text - 1) {
        clock_gettime(CLOCK_MONOTONIC, &t);
        long left = 5000 - (long)((t.tv_sec - t0.tv_sec) * 1000 + (t.tv_nsec - t0.tv_nsec) / 1000000);
        struct pollfd pf = {p[0], POLLIN, 0};
        if (left <= 0 || poll(&pf, 1, (int)left) <= 0) break;
        ssize_t r = read(p[0], text + n, sizeof text - 1 - n);
        if (r <= 0) break;
        n += (size_t)r;
    }
    text[n] = 0;
    close(p[0]);
    if (pid > 0) {
        kill(pid, SIGKILL);
        waitpid(pid, NULL, 0);
    }
    if (!strncmp(text, "systemd ", 8)) known = atoi(text + 8);
    return known;
}

typedef struct { const char *steam, *compatdata, *install, *proton, *runtime, *work, *exe, *token, *gpu; } Parts;

// held: the runtime's entry point, the Proton script and the warm's exe as checked, open until the launch has forked
static void launch(char **f, int nf, int *held)
{
    static char *env[MAX_ENV], *args[MAX_FIELDS], *argv[MAX_FIELDS + 24];
    static char roots[MAX_LIBS + 3][PATH_MAX], values[MAX_ENV][PATH_MAX];
    Parts p = {0};
    int nenv = 0, nargs = 0;
    if (nf % 2 != 1) REFUSE("bad request");
    for (int i = 1; i < nf; i += 2) {
        const char *k = f[i];
        const char **slot = !strcmp(k, "steam") ? &p.steam : !strcmp(k, "compatdata") ? &p.compatdata : !strcmp(k, "install") ? &p.install
                            : !strcmp(k, "proton") ? &p.proton : !strcmp(k, "runtime") ? &p.runtime : !strcmp(k, "work") ? &p.work
                            : !strcmp(k, "exe") ? &p.exe : !strcmp(k, "token") ? &p.token : !strcmp(k, "gpu") ? &p.gpu : NULL;
        if (slot) {
            if (*slot) REFUSE("%s: given twice", k);
            *slot = f[i + 1];
        } else if (!strcmp(k, "env")) {
            if (nenv == MAX_ENV) REFUSE("env: too many");
            env[nenv++] = f[i + 1];
        } else if (!strcmp(k, "arg")) args[nargs++] = f[i + 1];
        else REFUSE("%.64s: not a launch key", k);
    }
    if (!p.steam || !p.compatdata || !p.install || !p.proton || !p.runtime || !p.work || !p.exe || !p.token) REFUSE("a launch key is missing");
    find_roots();

    char real[PATH_MAX], compat[PATH_MAX], install[PATH_MAX], proton[PATH_MAX], script[PATH_MAX], runtime[PATH_MAX], entry[PATH_MAX];
    char work[PATH_MAX], prefix[PATH_MAX], exe[PATH_MAX], why[256];
    const char *rest;
    if (!steam[0] || !resolve(p.steam, real) || strcmp(real, steam)) REFUSE("steam: not Steam's folder");
    int lib = resolve(p.compatdata, compat) ? library_of(compat, "compatdata", &rest) : -1;
    if (lib < 0 || !digits(rest, 10)) REFUSE("compatdata: not an app's folder in a library's steamapps/compatdata");
    const char *app = rest;
    if (!resolve(p.install, install) || library_of(install, "common", &rest) != lib || strchr(rest, '/'))
        REFUSE("install: not a folder of steamapps/common in the prefix's library");

    // a tool in a library's steamapps/common or in a compatibilitytools.d folder, its script in there too
    int nroots = 0;
    for (int i = 0; i < nlibs; i++)
        if (library_folder(i, "common", roots[nroots])) nroots++;
    int libroots = nroots, proot = -1, rroot = -1;
    for (int i = 0; i < 3; i++)
        if (tools[i][0]) strcpy(roots[nroots++], tools[i]);
    if (resolve(p.proton, proton))
        for (int i = 0; i < nroots && proot < 0; i++)
            if (under(proton, roots[i])) proot = i;
    if (proot < 0 || !join(real, proton, "proton") || !resolve(real, script) || !under(script, roots[proot]) || (held[1] = safe_file(script, true)) < 0)
        REFUSE("proton: not a Proton in a Steam library or a compatibilitytools.d folder");
    if (resolve(p.runtime, runtime))
        for (int i = 0; i < libroots && rroot < 0; i++)
            if (under(runtime, roots[i])) rroot = i;
    if (rroot < 0 || !join(real, runtime, ENTRY_POINT) || !resolve(real, entry) || !under(entry, roots[rroot]) || (held[0] = safe_file(entry, true)) < 0)
        REFUSE("runtime: not a Steam Linux Runtime in a Steam library");

    if (!resolve(p.work, work) || !is_work(work)) REFUSE("work: not a work folder of the data folder's games");
    if (!join(prefix, compat, "pfx")) REFUSE("compatdata: too long");
    if (!win_to_host(prefix, p.exe, real) || !resolve(real, exe) || !is_native_warm(exe) || (held[2] = safe_file(exe, false)) < 0)
        REFUSE("exe: not the data folder's copy of " WARM_EXE);
    if (!token_form(p.token, true)) REFUSE("token: not a warm's token");
    if (p.gpu && !printable(p.gpu, 256)) REFUSE("gpu: not a device name");
    for (int i = 0; i < nenv; i++) {
        values[i][0] = 0;
        if (!cache_variable(env[i], values[i], why, sizeof why)) REFUSE("%s", why);
    }
    if (!warm_args(args, nargs, prefix, work, install, why, sizeof why)) REFUSE("%s", why);

    bool idle = false;
    for (int i = 2; i + 1 < nargs; i++)
        if (!strcmp(args[i], "--priority")) idle = !strcmp(args[i + 1], "idle");

    // ProtonLaunch.Environment's, with nothing of the sandbox's environment
    Env e = {0};
    env_base(&e);
    env_set(&e, "DXVK_LOG_PATH", "none");
    env_set(&e, "PROTON_LOG_DIR", work);
    env_set(&e, "STEAM_COMPAT_DATA_PATH", compat);
    env_set(&e, "STEAM_COMPAT_CLIENT_INSTALL_PATH", p.steam);
    env_set(&e, "STEAM_COMPAT_INSTALL_PATH", install);
    Buf list = {0};
    for (int i = 0; i < nlibs; i++) {
        if (i) put(&list, ":", 1);
        put(&list, libs[i].spelled, strlen(libs[i].spelled));
        put(&list, "/steamapps", 10);
    }
    put(&list, "", 1);
    env_set(&e, "STEAM_COMPAT_LIBRARY_PATHS", list.p);
    free(list.p);
    char pair[2 * PATH_MAX + 2];
    snprintf(pair, sizeof pair, "%s:%s", proton, runtime);
    env_set(&e, "STEAM_COMPAT_TOOL_PATHS", pair);
    env_set(&e, "STEAM_COMPAT_APP_ID", app);
    for (int i = 0; i < nenv; i++) {
        char name[72];
        snprintf(name, sizeof name, "%.*s", (int)(strchr(env[i], '=') - env[i]), env[i]);
        env_set(&e, name, values[i]);
    }
    if (p.gpu) {
        env_set(&e, "VKD3D_FILTER_DEVICE_NAME", p.gpu);
        env_set(&e, "DXVK_FILTER_DEVICE_NAME", p.gpu);
    }
    env_set(&e, TOKEN_VAR, p.token);

    const char *run = systemd_run();
    int version = run ? systemd_version(run, e.v) : 0;

    // the work folder as checked, the log in it never through a link and never a FIFO, whose open would wait for a reader
    int workfd = open(work, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC), log = -1;
    char fdpath[64], now[PATH_MAX], again[PATH_MAX];
    snprintf(fdpath, sizeof fdpath, "/proc/self/fd/%d", workfd);
    ssize_t len = workfd >= 0 ? readlink(fdpath, now, sizeof now - 1) : -1;
    if (len > 0) now[len] = 0;
    struct stat st;
    if (len <= 0 || strcmp(now, work)) fail("work: changed while it was checked");
    // pressure-vessel runs the script in a container that inherits no fd of this one, Proton finds its files from the
    // script's path and Wine opens the exe by its path: these can't be held past the exec, only checked again here
    else if (!still(held[1], script)) fail("proton: changed while it was checked");
    else if (!win_to_host(prefix, p.exe, real) || !resolve(real, again) || !still(held[2], again)) fail("exe: changed while it was checked");
    else if ((log = openat(workfd, "launcher.log", O_WRONLY | O_APPEND | O_CREAT | O_NOFOLLOW | O_NONBLOCK | O_CLOEXEC, 0644)) < 0)
        fail("work: launcher.log can't be opened (%s)", strerror(errno));
    else if (fstat(log, &st) != 0 || !S_ISREG(st.st_mode) || fcntl(log, F_SETFL, fcntl(log, F_GETFL) & ~O_NONBLOCK) != 0) {
        fail("work: launcher.log is not a file");
        close(log);
        log = -1;
    }
    char unit[64] = "", unitarg[80];
    unsigned char rnd[16];
    if (log >= 0 && run && syscall(SYS_getrandom, rnd, sizeof rnd, 0) != (long)sizeof rnd) {
        fail("no random unit name");
        close(log);
        log = -1;
    }
    if (log < 0) {
        if (workfd >= 0) close(workfd);
        env_free(&e);
        return;
    }

    int na = 0;
    if (run) {
        strcpy(unit, UNIT_PREFIX);
        for (int i = 0; i < 16; i++) sprintf(unit + strlen(unit), "%02x", rnd[i]);
        snprintf(unitarg, sizeof unitarg, "--unit=%s", unit);
        argv[na++] = (char *)run;
        argv[na++] = "--user";
        argv[na++] = "--scope";
        argv[na++] = "-q";
        argv[na++] = "--collect";
        argv[na++] = unitarg;
        argv[na++] = "-p";
        // CPUWeight=idle is systemd 251's, and from 254 systemd-run expands ${...} in a scope's command line itself
        argv[na++] = idle ? (version >= 251 ? "CPUWeight=idle" : "CPUWeight=1") : "CPUWeight=10";
        if (idle) {
            argv[na++] = "-p";
            argv[na++] = "IOWeight=1";
        }
        if (version >= 254) argv[na++] = "--expand-environment=no";
    }
    argv[na++] = ENTRY_FD;
    argv[na++] = "--verb=run";
    argv[na++] = "--";
    argv[na++] = script;
    argv[na++] = "run";
    argv[na++] = (char *)p.exe;
    for (int i = 0; i < nargs; i++) argv[na++] = args[i];
    argv[na] = NULL;

    Handle *grown = realloc(handles, (size_t)(nhandles + 1) * sizeof *handles);
    if (!grown) _exit(3);
    handles = grown;
    pid_t pid = fork();
    if (pid == 0) {
        // a session of its own: a terminal's Ctrl+C reaches the app alone
        setsid();
        dup2(devnull, 0);
        dup2(log, 1);
        dup2(log, 2);
        if (fchdir(workfd) != 0) _exit(126);
        // the entry point runs as the file checked, and its interpreter reads it through the fd
        if (dup2(held[0], 3) != 3 || fcntl(3, F_SETFD, 0) != 0) _exit(126);
        if (syscall(SYS_close_range, 4, ~0u, 0) != 0)
            for (int fd = 4; fd < 1024; fd++) close(fd);
        if (!run) {
            // without a scope: an absolute nice, and for idle the idle I/O class
            setpriority(PRIO_PROCESS, 0, idle ? 19 : 10);
            if (idle) syscall(SYS_ioprio_set, 1, 0, 3 << 13);
            // as Steam's reaper: what the launch orphans stays below it, where its signals reach, until it exits
            prctl(PR_SET_CHILD_SUBREAPER, 1);
        }
        sigset_t none;
        sigemptyset(&none);
        sigprocmask(SIG_SETMASK, &none, NULL);
        signal(SIGPIPE, SIG_DFL);
        execve(argv[0], argv, e.v);
        _exit(127);
    }
    int forked = errno;
    close(log);
    close(workfd);
    env_free(&e);
    if (pid < 0) REFUSE("fork: %s", strerror(forked));
    Handle *h = &handles[nhandles];
    memset(h, 0, sizeof *h);
    h->pid = pid;
    h->scoped = run != NULL;
    strcpy(h->token, p.token);
    ok();
    number_field(&out, nhandles++);
    field(&out, unit);
}

static void op_launch(char **f, int nf)
{
    int held[3] = {-1, -1, -1};
    launch(f, nf, held);
    for (int i = 0; i < 3; i++)
        if (held[i] >= 0) close(held[i]);
}

// reaps the launch's first process once it has exited; true until then
static bool running(Handle *h)
{
    if (!h->exited) {
        int st;
        pid_t r = waitpid(h->pid, &st, WNOHANG);
        if (r == h->pid) {
            h->exited = true;
            h->code = WIFEXITED(st) ? WEXITSTATUS(st) : 128 + WTERMSIG(st);
        } else if (r < 0 && errno == ECHILD) {
            h->exited = true;
            h->code = -1;
        }
    }
    return !h->exited;
}

static void op_status(char **f)
{
    long long i;
    if (!number(f[1], INT_MAX, &i) || i >= nhandles) REFUSE("no such handle");
    Handle *h = &handles[i];
    running(h);
    ok();
    if (!h->exited) {
        field(&out, "running");
        return;
    }
    field(&out, "exited");
    number_field(&out, h->code);
}

// systemd's name for a warm's scope: scskiller-warm-<32 hex>.scope
static bool warm_scope(const char *part)
{
    size_t n = strlen(UNIT_PREFIX);
    return !strncmp(part, UNIT_PREFIX, n) && strlen(part) == n + 32 + 6 && hex(part + n, 32) && !strcmp(part + n + 32, ".scope");
}

// pid is below ancestor in the process tree: an orphan is no longer below what started it
static bool descends(long long pid, pid_t ancestor)
{
    for (int depth = 0; depth < 4096 && pid > 1; depth++) {
        int dfd = open_pid(pid);
        Stat st;
        bool got = dfd >= 0 && read_stat(dfd, &st);
        if (dfd >= 0) close(dfd);
        if (!got) return false;
        if (st.ppid == ancestor) return true;
        pid = st.ppid;
    }
    return false;
}

// a warm's process: every uid of it this user's, Wine running the data folder's warm copy or a staged child, the token
// in its environment, in a warm's scope or, without systemd, below a running launch of this helper's with that token
static bool warm_process(int dfd, long long pid, const char *token)
{
    static Buf b;
    if (!ours(dfd) || !slurp(dfd, "status", &b, 65536)) return false;
    const char *uids = strstr(b.p, "\nUid:");
    unsigned long id[4];
    if (!uids || sscanf(uids + 5, "%lu %lu %lu %lu", &id[0], &id[1], &id[2], &id[3]) != 4) return false;
    for (int i = 0; i < 4; i++)
        if (id[i] != uid) return false;
    char exe[PATH_MAX], name[NAME_MAX + 8], image[PATH_MAX], has[96];
    if (!read_exe(dfd, exe) || !is_wine(exe) || !read_token(dfd, has, sizeof has) || strcmp(has, token)) return false;
    if (!windows_name(dfd, name, sizeof name) || !wine_image(dfd, name, image) || !(is_native_warm(image) || is_staged(image))) return false;
    if (!slurp(dfd, "cgroup", &b, 65536)) return false;
    // cgroup v2's line, 0::<path>
    const char *cg = !strncmp(b.p, "0::", 3) ? b.p : strstr(b.p, "\n0::");
    if (cg) {
        char path[PATH_MAX];
        cg += cg == b.p ? 3 : 4;
        snprintf(path, sizeof path, "%.*s", (int)strcspn(cg, "\n"), cg);
        for (char *part = strtok(path, "/"); part; part = strtok(NULL, "/"))
            if (warm_scope(part)) return true;
    }
    for (int i = 0; i < nhandles; i++)
        if (!handles[i].scoped && !strcmp(handles[i].token, token) && running(&handles[i]) && descends(pid, handles[i].pid)) return true;
    return false;
}

static void op_signal(char **f)
{
    long long pid;
    if (!number(f[1], INT_MAX, &pid) || pid < 1) REFUSE("bad request");
    int sig = !strcmp(f[2], "KILL") ? SIGKILL : !strcmp(f[2], "STOP") ? SIGSTOP : !strcmp(f[2], "CONT") ? SIGCONT : 0;
    if (!sig) REFUSE("signal: not allowed");
    if (!token_form(f[3], true)) REFUSE("token: not a warm's token");
    find_roots();
    // the pidfd before the checks: a pid freed and reused after them names another process, which the pidfd doesn't reach
    int pfd = (int)syscall(SYS_pidfd_open, (int)pid, 0);
    if (pfd < 0) {
        if (errno == ESRCH) REFUSE("gone");
        REFUSE("no pidfd: %s", strerror(errno));
    }
    int dfd = open_pid(pid);
    Stat st;
    // a zombie keeps its pid until it's reaped, and no longer its exe or environment
    bool dead = dfd < 0 || (read_stat(dfd, &st) && (st.state == 'Z' || st.state == 'X'));
    bool warm = !dead && warm_process(dfd, pid, f[3]);
    if (dfd >= 0) close(dfd);
    long r = warm ? syscall(SYS_pidfd_send_signal, pfd, sig, NULL, 0) : -1;
    int why = errno;
    close(pfd);
    if (dead) REFUSE("gone");
    if (!warm) REFUSE("not ours");
    if (r != 0) {
        if (why == ESRCH) REFUSE("gone");
        REFUSE("signal: %s", strerror(why));
    }
    ok();
}

int main(void)
{
    signal(SIGPIPE, SIG_IGN);
    uid = getuid();
    proc = open("/proc", O_RDONLY | O_DIRECTORY | O_CLOEXEC);
    devnull = open("/dev/null", O_RDWR | O_CLOEXEC);
    bool hello = false;
    static char req[MAX_REQUEST];
    static char *f[MAX_FIELDS];
    for (;;) {
        unsigned char len[4];
        size_t got = read_all(0, (char *)len, 4);
        if (got == 0) return 0;
        if (got < 4) malformed();
        uint32_t n = (uint32_t)len[0] | (uint32_t)len[1] << 8 | (uint32_t)len[2] << 16 | (uint32_t)len[3] << 24;
        if (n == 0 || n > MAX_REQUEST || read_all(0, req, n) < n || req[n - 1] != 0) malformed();
        int nf = 0;
        for (uint32_t at = 0; at < n; at += (uint32_t)strlen(req + at) + 1) {
            if (nf == MAX_FIELDS) malformed();
            f[nf++] = req + at;
        }
        const char *op = f[0];
        if (!hello) {
            if (strcmp(op, "hello") || nf != 2) fail("hello first");
            else if (strcmp(f[1], PROTOCOL)) fail("unsupported version");
            else if (uid == 0 || geteuid() != uid) fail("not for root");
            else if (proc < 0 || devnull < 0) fail("no /proc");
            else if (!find_home()) fail("no home folder");
            else {
                hello = true;
                ok();
                field(&out, PROTOCOL);
            }
            reply();
            if (!hello) return 2;
            continue;
        }
        if (!strcmp(op, "whoami")) nf == 3 ? op_whoami(f) : fail("bad request");
        else if (!strcmp(op, "snapshot")) nf == 1 ? op_snapshot() : fail("bad request");
        else if (!strcmp(op, "image")) nf == 2 ? op_image(f) : fail("bad request");
        else if (!strcmp(op, "argv")) nf == 2 ? op_argv(f) : fail("bad request");
        else if (!strcmp(op, "launch")) op_launch(f, nf);
        else if (!strcmp(op, "status")) nf == 2 ? op_status(f) : fail("bad request");
        else if (!strcmp(op, "signal")) nf == 4 ? op_signal(f) : fail("bad request");
        else if (!strcmp(op, "hello")) fail("hello again");
        else fail("unknown operation");
        reply();
    }
}
