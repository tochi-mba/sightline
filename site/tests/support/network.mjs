// The release API as the tests need it: fixtures read from disk, and a fetch that answers from them.
import { readFile } from "node:fs/promises";

/** A release fixture, parsed afresh for each caller so no test can change another's copy. */
export async function fixture(name) {
  return JSON.parse(await readFile(new URL(`../fixtures/${name}.json`, import.meta.url), "utf8"));
}

const RELEASES = "https://api.github.com/repos/tochi-mba/sightline/releases/latest";

/**
 * A fetch that answers the latest-release API from `answers.latest`, records every request, and
 * rejects anything it has no answer for, as a browser does when it is offline. An answer is a
 * release document; an HTTP status the API refused with, such as 403 when it rate-limits a visitor;
 * an Error to reject the request with; or "not JSON", for a body that does not parse.
 */
export function fakeFetch(answers) {
  const requests = [];
  const fetch = async (url, init) => {
    requests.push({ url, init });
    const answer = url === RELEASES ? answers.latest : undefined;
    if (answer === undefined) throw new TypeError("Failed to fetch");
    if (answer instanceof Error) throw answer;
    if (typeof answer === "number") {
      return { ok: false, status: answer, json: async () => ({ message: "Refused" }) };
    }
    if (answer === "not JSON") {
      return {
        ok: true,
        status: 200,
        json: async () => {
          throw new SyntaxError("Unexpected token '<', \"<!doctype \"... is not valid JSON");
        },
      };
    }
    return { ok: true, status: 200, json: async () => answer };
  };
  return { fetch, requests };
}
