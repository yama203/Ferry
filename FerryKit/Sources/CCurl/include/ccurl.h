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

// SFTP のホスト鍵の照合の結果。CURLOPT_SSH_KEYDATA でコールバックに渡し、接続の後に呼び出し側が読む。
// 鍵はサーバーが出した公開鍵（ssh の鍵の形式のバイト列）。これより長い鍵は受け入れない
#define CCURL_HOSTKEY_MAX 16384
typedef struct {
    // この接続でコールバックが呼ばれたか（再利用した接続では呼ばれない）
    int checked;
    // known_hosts との照合の結果（enum curl_khmatch）
    int match;
    // 接続を拒否したか
    int rejected;
    // 承認済みの鍵を受け入れて known_hosts に記録したか（呼び出し側が読んだら 0 に戻す）
    int accepted_pinned;
    unsigned char found_key[CCURL_HOSTKEY_MAX];
    size_t found_len;
    // 承認済みの鍵。ユーザーが画面で見て承認した鍵だけを、記録と食い違っていても受け入れる（0 なら無し）
    unsigned char pinned_key[CCURL_HOSTKEY_MAX];
    size_t pinned_len;
} ccurl_hostkey_state;

// SFTP のホスト鍵: 未知のホストは記録して接続、記録済みの鍵と食い違う場合は拒否する。
// state->pinned_key と同じ鍵なら、食い違っていても known_hosts の記録をその鍵に置き換えて接続する。
CURLcode ccurl_use_known_hosts(CURL *handle, const char *path, ccurl_hostkey_state *state);
// 次の接続の前に照合の結果を消す（承認済みの鍵と accepted_pinned は残す）
void ccurl_hostkey_state_clear(ccurl_hostkey_state *state);
// サーバーが出した鍵（長さを len に入れる。無ければ NULL）。Swift からは大きい配列を直接読めないため
const unsigned char *ccurl_hostkey_state_found_key(const ccurl_hostkey_state *state, size_t *len);
// 承認済みの鍵を設定する（len が 0 なら解除）。長すぎる鍵は設定しない
void ccurl_hostkey_state_pin(ccurl_hostkey_state *state, const unsigned char *key, size_t len);

// 直前の接続のサーバー証明書（先頭の 1 枚）の Subject と Subject Alternative Name の行を、改行区切りで out に入れる。
// CURLOPT_CERTINFO を有効にして接続した後に呼ぶ。戻り値は書いた長さ（証明書の情報が無ければ 0）
size_t ccurl_leaf_certificate_names(CURL *handle, char *out, size_t size);

// 通信ログを stderr に出す。PASS コマンドは伏せ字にする。
CURLcode ccurl_enable_debug_log(CURL *handle);

#endif
