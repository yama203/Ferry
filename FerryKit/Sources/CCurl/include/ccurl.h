#ifndef CCURL_H
#define CCURL_H

// SDK 同梱の古い curl.h ではなく Homebrew 版を確実に読むため絶対パスで指定する。
#include "/opt/homebrew/opt/curl/include/curl/curl.h"

// curl_easy_setopt は可変長引数で Swift から直接呼べないため、型ごとのラッパーを用意する。
typedef size_t (*ccurl_io_cb)(char *ptr, size_t size, size_t nmemb, void *userdata);
typedef int (*ccurl_xferinfo_cb)(void *userdata, curl_off_t dltotal, curl_off_t dlnow,
                                 curl_off_t ultotal, curl_off_t ulnow);

CURLcode ccurl_global_init(void);

CURLcode ccurl_setopt_long(CURL *handle, CURLoption option, long value);
CURLcode ccurl_setopt_off(CURL *handle, CURLoption option, curl_off_t value);
CURLcode ccurl_setopt_string(CURL *handle, CURLoption option, const char *value);
CURLcode ccurl_setopt_pointer(CURL *handle, CURLoption option, void *value);
CURLcode ccurl_setopt_io(CURL *handle, CURLoption option, ccurl_io_cb callback);
CURLcode ccurl_setopt_xferinfo(CURL *handle, ccurl_xferinfo_cb callback);

CURLcode ccurl_getinfo_long(CURL *handle, CURLINFO info, long *out);
CURLcode ccurl_getinfo_string(CURL *handle, CURLINFO info, const char **out);

// SFTP のホスト鍵: 未知のホストは記録して接続、記録済みの鍵と食い違う場合は拒否する。
CURLcode ccurl_use_known_hosts(CURL *handle, const char *path);

// 通信ログを stderr に出す。PASS コマンドは伏せ字にする。
CURLcode ccurl_enable_debug_log(CURL *handle);

#endif
