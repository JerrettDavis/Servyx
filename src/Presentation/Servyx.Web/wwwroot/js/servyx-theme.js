// Theme persistence and resolution. Loaded normally (non-blocking) — the *before-paint*
// work is done by the inline bootstrap in App.razor / LoginPage.razor, which deliberately
// duplicates the small resolve step so it needs no network fetch.
window.servyxTheme = {
    storageKey: 'svx-theme',

    /** 'system' | 'light' | 'dark' — whatever the operator last chose. */
    read: function () {
        try {
            var c = localStorage.getItem(window.servyxTheme.storageKey);
            return (c === 'light' || c === 'dark') ? c : 'system';
        } catch (_) {
            return 'system';
        }
    },

    /** Collapses a choice to a concrete 'light' | 'dark'. */
    resolve: function (choice) {
        if (choice === 'light' || choice === 'dark') { return choice; }
        return (window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches)
            ? 'dark' : 'light';
    },

    /** Writes both attributes on <html>. Returns the resolved theme. */
    apply: function (choice) {
        var c = (choice === 'light' || choice === 'dark' || choice === 'system')
            ? choice
            : window.servyxTheme.read();
        var r = window.servyxTheme.resolve(c);
        var e = document.documentElement;
        e.setAttribute('data-theme', r);
        e.setAttribute('data-theme-choice', c);
        e.style.colorScheme = r;
        return r;
    },

    /** Persists then applies. Returns the resolved theme. */
    set: function (choice) {
        try { localStorage.setItem(window.servyxTheme.storageKey, choice); } catch (_) { }
        return window.servyxTheme.apply(choice);
    },
};

// While the choice is 'system', follow the OS live.
if (window.matchMedia) {
    window.matchMedia('(prefers-color-scheme: dark)').addEventListener('change', function () {
        if (window.servyxTheme.read() === 'system') { window.servyxTheme.apply('system'); }
    });
}

// Enhanced navigation swaps <head>; <html> attributes survive, but re-asserting is free.
document.addEventListener('enhancedload', function () { window.servyxTheme.apply(); });
