youtubei.js = YouTube.js 18.3.0 (github.com/LuanRT/YouTube.js, MIT, see LICENSE-youtubei.txt): the npm package's
bundle/browser.js, unchanged except the source-map line removed. version.txt holds its version.
Used by Browser/ytweb/resolver.js (the YouTube web route) to decipher stream urls.

To update it, run tools\update-youtubejs.ps1: it downloads the newest version, checks that real videos play with it,
and puts the old one back if they don't. tools\update-youtubejs.ps1 -TestOnly only checks the current copy.
