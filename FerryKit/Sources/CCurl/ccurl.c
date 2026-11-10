#include "ccurl.h"
#include <stdio.h>
#include <string.h>

CURLcode ccurl_global_init(void) {
    return curl_global_init(CURL_GLOBAL_DEFAULT);
}

CURLcode ccurl_setopt_long(CURL *handle, CURLoption option, long value) {
    return curl_easy_setopt(handle, option, value);
}

CURLcode ccurl_setopt_off(CURL *handle, CURLoption option, curl_off_t value) {
    return curl_easy_setopt(handle, option, value);
}

CURLcode ccurl_setopt_string(CURL *handle, CURLoption option, const char *value) {
    return curl_easy_setopt(handle, option, value);
}

CURLcode ccurl_setopt_pointer(CURL *handle, CURLoption option, void *value) {
    return curl_easy_setopt(handle, option, value);
}

CURLcode ccurl_setopt_io(CURL *handle, CURLoption option, ccurl_io_cb callback) {
    return curl_easy_setopt(handle, option, callback);
}

CURLcode ccurl_setopt_xferinfo(CURL *handle, ccurl_xferinfo_cb callback) {
    return curl_easy_setopt(handle, CURLOPT_XFERINFOFUNCTION, callback);
}

CURLcode ccurl_getinfo_long(CURL *handle, CURLINFO info, long *out) {
    return curl_easy_getinfo(handle, info, out);
}

CURLcode ccurl_getinfo_string(CURL *handle, CURLINFO info, const char **out) {
    char *value = NULL;
    CURLcode rc = curl_easy_getinfo(handle, info, &value);
    *out = value;
    return rc;
}

static int known_host_callback(CURL *easy, const struct curl_khkey *knownkey,
                               const struct curl_khkey *foundkey, enum curl_khmatch match,
                               void *clientp) {
    (void)easy; (void)knownkey;
    ccurl_hostkey_state *state = clientp;
    if (!state) {
        switch (match) {
            case CURLKHMATCH_OK:      return CURLKHSTAT_FINE;
            case CURLKHMATCH_MISSING: return CURLKHSTAT_FINE_ADD_TO_FILE;
            default:                  return CURLKHSTAT_REJECT;
        }
    }
    state->checked = 1;
    state->match = match;
    state->found_len = 0;
    // libssh2 版の libcurl はサーバーの鍵をそのままのバイト列（len > 0）で渡す。base64 の形（len == 0）は扱わない
    if (foundkey && foundkey->key && foundkey->len > 0 && foundkey->len <= CCURL_HOSTKEY_MAX) {
        memcpy(state->found_key, foundkey->key, foundkey->len);
        state->found_len = foundkey->len;
    }
    int pinned = state->pinned_len > 0 && state->found_len == state->pinned_len
        && memcmp(state->found_key, state->pinned_key, state->pinned_len) == 0;
    switch (match) {
        case CURLKHMATCH_OK:
            return CURLKHSTAT_FINE;
        case CURLKHMATCH_MISSING:
            // 承認した鍵があるのに別の鍵が来たときは、初めてのホストとしても受け入れない
            if (state->pinned_len > 0 && !pinned) break;
            if (pinned) state->accepted_pinned = 1;
            return CURLKHSTAT_FINE_ADD_TO_FILE;
        case CURLKHMATCH_MISMATCH:
            if (!pinned) break;
            // 記録と食い違っているが、ユーザーが承認した鍵: 古い記録を消して新しい鍵を書く
            state->accepted_pinned = 1;
            return CURLKHSTAT_FINE_REPLACE;
        default:
            break;
    }
    state->rejected = 1;
    return CURLKHSTAT_REJECT;
}

CURLcode ccurl_use_known_hosts(CURL *handle, const char *path, ccurl_hostkey_state *state) {
    CURLcode rc = curl_easy_setopt(handle, CURLOPT_SSH_KNOWNHOSTS, path);
    if (rc != CURLE_OK) return rc;
    rc = curl_easy_setopt(handle, CURLOPT_SSH_KEYDATA, (void *)state);
    if (rc != CURLE_OK) return rc;
    return curl_easy_setopt(handle, CURLOPT_SSH_KEYFUNCTION, known_host_callback);
}

void ccurl_hostkey_state_clear(ccurl_hostkey_state *state) {
    state->checked = 0;
    state->match = 0;
    state->rejected = 0;
    state->found_len = 0;
}

const unsigned char *ccurl_hostkey_state_found_key(const ccurl_hostkey_state *state, size_t *len) {
    *len = state->found_len;
    return state->found_len > 0 ? state->found_key : NULL;
}

void ccurl_hostkey_state_pin(ccurl_hostkey_state *state, const unsigned char *key, size_t len) {
    if (!key || len == 0 || len > CCURL_HOSTKEY_MAX) {
        state->pinned_len = 0;
        return;
    }
    memcpy(state->pinned_key, key, len);
    state->pinned_len = len;
}

size_t ccurl_leaf_certificate_names(CURL *handle, char *out, size_t size) {
    struct curl_certinfo *info = NULL;
    if (size == 0) return 0;
    out[0] = 0;
    if (curl_easy_getinfo(handle, CURLINFO_CERTINFO, &info) != CURLE_OK || !info || info->num_of_certs < 1) return 0;
    size_t used = 0;
    for (struct curl_slist *item = info->certinfo[0]; item; item = item->next) {
        const char *line = item->data;
        if (strncmp(line, "Subject:", 8) != 0 && strncmp(line, "X509v3 Subject Alternative Name:", 32) != 0) continue;
        int written = snprintf(out + used, size - used, "%s\n", line);
        if (written < 0 || (size_t)written >= size - used) break;
        used += (size_t)written;
    }
    return used;
}

static int debug_callback(CURL *handle, curl_infotype type, char *data, size_t size, void *userp) {
    (void)handle; (void)userp;
    const char *prefix;
    switch (type) {
        case CURLINFO_TEXT:       prefix = "* "; break;
        case CURLINFO_HEADER_IN:  prefix = "< "; break;
        case CURLINFO_HEADER_OUT: prefix = "> "; break;
        default: return 0;
    }
    if (type == CURLINFO_HEADER_OUT && size >= 5 && strncmp(data, "PASS ", 5) == 0) {
        fputs("> PASS ********\n", stderr);
        return 0;
    }
    fprintf(stderr, "%s%.*s", prefix, (int)size, data);
    return 0;
}

CURLcode ccurl_enable_debug_log(CURL *handle) {
    CURLcode rc = curl_easy_setopt(handle, CURLOPT_DEBUGFUNCTION, debug_callback);
    if (rc != CURLE_OK) return rc;
    return curl_easy_setopt(handle, CURLOPT_VERBOSE, 1L);
}
