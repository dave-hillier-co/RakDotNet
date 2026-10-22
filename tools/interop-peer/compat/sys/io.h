/* RakNet's FileList.cpp includes <sys/io.h> but uses nothing from it. glibc provides the header on x86 and not on
   arm64, so this empty file stands in when the system has none. */
