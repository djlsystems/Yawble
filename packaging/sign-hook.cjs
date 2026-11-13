// electron-builder's signtoolOptions.sign: the app's files and the installer, signed like the payload (windows-signing.cjs).
'use strict';
module.exports = require('./windows-signing.cjs').electronBuilderSign;
