/*
  `node --test site/tests` is how these tests run, here and in the Pages workflow.

  Node 20 searched a folder named that way for test files, such as the *.test.mjs here, and ran each
  in its own process. Node 22 and later read the argument as a glob instead, which matches only the
  folder itself, and then run the folder as a module: this file. So it imports every test file in the
  folder, and the one command runs the same tests on either. Node 20 never runs this file, because
  "index" is not a test file's name.
*/
"use strict";

const { readdirSync } = require("node:fs");
const { join } = require("node:path");
const { pathToFileURL } = require("node:url");

const files = readdirSync(__dirname)
  .filter((name) => name.endsWith(".test.mjs"))
  .sort();

(async () => {
  for (const name of files) await import(pathToFileURL(join(__dirname, name)).href);
})().catch((error) => {
  // A test file that cannot even load is a failure, said plainly rather than as a stray rejection.
  console.error(error);
  process.exitCode = 1;
});
