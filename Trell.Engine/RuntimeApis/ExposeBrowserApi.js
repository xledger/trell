async function fetch(url, options) {
    let response = await dotNetFetch.Fetch(url, options);
    return {
        ok: response.ok,
        status: response.status,
        statusText: response.statusText,
        text: async () => response.text(),
        headers: new Headers(response.headers)
    };
}

function Headers(dotNetHeaders) {
    this.headers = dotNetHeaders;
    this.get = function (key) {
        return this.headers[key];
    }
}
