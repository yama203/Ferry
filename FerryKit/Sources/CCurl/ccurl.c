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
    (void)easy; (void)knownkey; (void)foundkey; (void)clientp;
    switch (match) {
        case CURLKHMATCH_OK:      return CURLKHSTAT_FINE;
        case CURLKHMATCH_MISSING: return CURLKHSTAT_FINE_ADD_TO_FILE;
        default:                  return CURLKHSTAT_REJECT;
    }
}

CURLcode ccurl_use_known_hosts(CURL *handle, const char *path) {
    CURLcode rc = curl_easy_setopt(handle, CURLOPT_SSH_KNOWNHOSTS, path);
    if (rc != CURLE_OK) return rc;
    return curl_easy_setopt(handle, CURLOPT_SSH_KEYFUNCTION, known_host_callback);
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
