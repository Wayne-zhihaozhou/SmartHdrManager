// ==UserScript==
// @name         YouTube Force & Detect True HDR for SmartHdrManager
// @namespace    smarthdrmanager
// @version      3.1
// @description  Marks the tab title while a video plays: " [real_hdr_active]" for real HDR YouTube videos, " [sdr_video_active]" for other videos (RTX Video HDR)
// @match        *://*/*
// @run-at       document-start
// @noframes
// @grant        unsafeWindow
// ==/UserScript==

(function () {
    'use strict';

    const win = typeof unsafeWindow !== 'undefined' ? unsafeWindow : window;

    const HDR_TAG = ' [real_hdr_active]';        // must match BrowserRealHdrToken in config.json
    const SDR_TAG = ' [sdr_video_active]';       // must match BrowserSdrVideoToken in config.json
    const TAG_RE = /\s*\[(real_hdr_active|sdr_video_active)\]/gi;
    const host = win.location.hostname;
    const IS_YOUTUBE = /(^|\.)youtube\.com$/i.test(host);
    const IS_YOUTUBE_MUSIC = /^music\.youtube\.com$/i.test(host);   // music: never an SDR video tag
    const MIN_VIDEO_AREA = 0.25;                 // other sites: the video must cover a quarter of the window (no previews/ads)
    const PAUSE_GRACE_MS = 10 * 60000;           // a paused video keeps its tag this long while its tab is open and visible
                                                 // (no HDR/SDR switch for a pause; leaving the video removes it at once)
    const FALLBACK_INTERVAL_MS = 1000;           // safety net; normal updates are event-driven

    // ---------------------------------------------------------------------
    // 1. SPOOFING: make YouTube believe the display is already in HDR before the player loads,
    //    so it serves the real HDR stream even while Windows is still in SDR. (unchanged)
    // ---------------------------------------------------------------------
    const origMatchMedia = IS_YOUTUBE && win.matchMedia ? win.matchMedia.bind(win) : null;
    if (origMatchMedia) {
        win.matchMedia = function (query) {
            if (typeof query === 'string' &&
                (query.includes('dynamic-range: high') ||
                 query.includes('video-dynamic-range: high') ||
                 query.includes('color-gamut: p3') ||
                 query.includes('color-gamut: rec2020'))) {
                return {
                    matches: true,
                    media: query,
                    onchange: null,
                    addListener: function () {},
                    removeListener: function () {},
                    addEventListener: function () {},
                    removeEventListener: function () {},
                    dispatchEvent: function () { return false; }
                };
            }
            return origMatchMedia(query);
        };
    }

    if (IS_YOUTUBE) {
        try {
            Object.defineProperty(win.screen, 'colorDepth', { get: () => 30, configurable: true });
            Object.defineProperty(win.screen, 'pixelDepth', { get: () => 30, configurable: true });
        } catch (e) {}
    }

    // ---------------------------------------------------------------------
    // 2. DETECTION
    // ---------------------------------------------------------------------

    // a) Does this video offer HDR at all? Cached per video ID, so the format list is not re-scanned every second.
    let cachedVideoId = null;
    let cachedHasHdr = false;

    function videoHasHdrFormats(player) {
        let videoId = null;
        try { videoId = player.getVideoData?.()?.video_id || null; } catch (e) {}
        if (videoId && videoId === cachedVideoId) return cachedHasHdr;

        let hdr = false;
        let formatsLoaded = false;
        try {
            // Check A: HDR quality levels in the quality menu
            if (typeof player.getAvailableQualityData === 'function') {
                const qualities = player.getAvailableQualityData() || [];
                hdr = qualities.some(q => q.qualityLabel && q.qualityLabel.toUpperCase().includes('HDR'));
            }

            // Check B: transfer function / codec (PQ = ST2084, HLG = ARIB_STD_B67, VP9 profile 2 = vp09.02)
            if (!hdr && typeof player.getPlayerResponse === 'function') {
                const formats = player.getPlayerResponse()?.streamingData?.adaptiveFormats || [];
                formatsLoaded = formats.length > 0;
                hdr = formats.some(f =>
                    (f.colorInfo &&
                        (f.colorInfo.transferCharacteristics === 'COLOR_TRANSFER_ST2084' ||
                         f.colorInfo.transferCharacteristics === 'COLOR_TRANSFER_ARIB_STD_B67')) ||
                    (f.mimeType && (f.mimeType.includes('vp09.02') || f.mimeType.includes('09.16.09.0')))
                );
            }
        } catch (e) {}

        // Only cache once the player actually has the metadata; otherwise check again next time
        if (videoId && (hdr || formatsLoaded)) {
            cachedVideoId = videoId;
            cachedHasHdr = hdr;
        }
        return hdr;
    }

    // b) Is the stream that is playing right now HDR? Uses "Stats for nerds" codec info when available.
    //    Returns null when unknown, then the availability check (a) decides.
    function playingStreamIsHdr(player) {
        try {
            const codecs = player.getStatsForNerds?.()?.codecs;
            if (typeof codecs === 'string') {
                const c = codecs.toLowerCase();
                // Only trust full codec strings such as "vp09.02.51.10.01.09.16.09.00 (337)"
                if (/\b(vp09|av01|avc1|hev1|hvc1)\./.test(c)) {
                    return c.includes('vp09.02') || /\bav01\.\d\.\d+[mh]\.10\b/.test(c);
                }
            }
        } catch (e) {}
        return null;
    }

    // c) Is the video playing? 1 = playing, 3 = buffering, 2 = paused, 0 = ended, -1 = unstarted, 5 = cued
    //    A paused video keeps its tag (HDR and SDR) while its tab is visible, for up to PAUSE_GRACE_MS: pausing does
    //    not switch HDR off and on again. Ending the video, leaving it or hiding the tab removes the tag at once, then
    //    SmartHdrManager waits its own "back to SDR after the video stops" delay.
    let lastPlayingAt = 0;

    function playbackState(player) {
        let state = -1;
        try { state = player.getPlayerState?.() ?? -1; } catch (e) {}

        if (state === 1 || state === 3) {
            lastPlayingAt = Date.now();
            return 'playing';
        }
        if (state === 2 && lastPlayingAt > 0 && Date.now() - lastPlayingAt < PAUSE_GRACE_MS &&
            document.visibilityState === 'visible') {
            return 'paused';
        }
        return 'stopped';
    }

    let wantTag = '';   // '', 'hdr' or 'sdr'

    function computeYouTubeTag() {
        if (!win.location.pathname.startsWith('/watch')) return '';

        const player = document.getElementById('movie_player');
        if (!player) return '';

        // During ads keep the current state: no switch before a pre-roll, no HDR->SDR->HDR flicker at a mid-roll
        if (player.classList.contains('ad-showing')) return wantTag;

        const state = playbackState(player);
        if (state === 'stopped') return '';

        const playing = playingStreamIsHdr(player);
        const hdr = playing !== null ? playing : videoHasHdrFormats(player);
        if (hdr) return 'hdr';
        return !IS_YOUTUBE_MUSIC ? 'sdr' : '';   // playing, or paused for a moment (see playbackState)
    }

    // d) Any other site: a large, audible video that is playing (SDR; HDR cannot be detected reliably there).
    //    Paused: keeps the tag like on YouTube while the tab is visible and the video played shortly before.
    let genericLastPlayingAt = 0;

    function computeGenericTag() {
        const videos = document.getElementsByTagName('video');
        if (videos.length === 0) return '';
        const fs = document.fullscreenElement;
        const viewArea = Math.max(1, win.innerWidth * win.innerHeight);
        let pausedBig = false;
        for (const v of videos) {
            if (v.ended || v.readyState < 2 || v.muted || v.volume === 0) continue;
            const r = v.getBoundingClientRect();
            const big = (fs && (fs === v || fs.contains(v))) || (r.width * r.height) / viewArea >= MIN_VIDEO_AREA;
            if (!big) continue;
            if (!v.paused) {
                genericLastPlayingAt = Date.now();
                return 'sdr';
            }
            if (v.currentTime > 0) pausedBig = true;
        }
        if (pausedBig && genericLastPlayingAt > 0 && Date.now() - genericLastPlayingAt < PAUSE_GRACE_MS &&
            document.visibilityState === 'visible') {
            return 'sdr';
        }
        return '';
    }

    function computeWantTag() {
        return IS_YOUTUBE ? computeYouTubeTag() : computeGenericTag();
    }

    // ---------------------------------------------------------------------
    // 3. TAG SYNCHRONISATION FOR SMARTHDRMANAGER
    // ---------------------------------------------------------------------
    function applyTitle() {
        const title = document.title || '';
        const clean = title.replace(TAG_RE, '');
        const desired = wantTag === 'hdr' ? clean + HDR_TAG : wantTag === 'sdr' ? clean + SDR_TAG : clean;
        if (title !== desired) document.title = desired;
    }

    // YouTube rewrites the title (navigation, notification count). Re-apply the tag immediately instead of
    // waiting for the next check, so SmartHdrManager never sees a title without the tag in between.
    let observedTitle = null;
    const titleObserver = new MutationObserver(applyTitle);

    function watchTitleElement() {
        const el = document.querySelector('head > title');
        if (el && el !== observedTitle) {
            titleObserver.disconnect();
            titleObserver.observe(el, { childList: true, characterData: true, subtree: true });
            observedTitle = el;
        }
    }

    function update() {
        wantTag = computeWantTag();
        applyTitle();
        watchTitleElement();
    }

    // Event-driven updates. Media events do not bubble, but they can be caught in the capture phase.
    // The short delay lets the YouTube player update its own state first.
    const updateSoon = () => setTimeout(update, 50);
    for (const ev of ['playing', 'pause', 'ended', 'emptied', 'volumechange']) {
        document.addEventListener(ev, updateSoon, true);
    }
    document.addEventListener('fullscreenchange', updateSoon);
    document.addEventListener('yt-navigate-start', () => { lastPlayingAt = 0; }, true);
    document.addEventListener('yt-navigate-finish', updateSoon, true);
    document.addEventListener('visibilitychange', updateSoon);

    setInterval(update, FALLBACK_INTERVAL_MS);
})();
