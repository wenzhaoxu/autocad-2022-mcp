import assert from "node:assert/strict";
import { performance } from "node:perf_hooks";
import { buildModelRecipe } from "../dist/src/recipe.js";

const featureCount = 500;
const iterations = 25;
const features = Array.from({ length: featureCount }, (_, index) => ({
  id: `part-${index}`,
  type: "box",
  origin: [index * 2, 0, 0],
  length: { param: "size" },
  width: 1,
  height: 1,
}));

let cadCalls = 0;
let nextHandle = 1;
const bridge = {
  async call(method, params) {
    assert.equal(method, "solid");
    assert.equal(params.action, "create");
    cadCalls += 1;
    return {
      created: params.primitives.map(() => ({ handle: (nextHandle++).toString(16).toUpperCase() })),
    };
  },
};

const samples = [];
for (let iteration = 0; iteration < iterations; iteration += 1) {
  const started = performance.now();
  const result = await buildModelRecipe(bridge, {
    recipe: { id: "orchestration-bench", units: "mm", parameters: { size: 1 }, features },
  });
  samples.push(performance.now() - started);
  assert.equal(result.liveHandles.length, featureCount);
}

samples.sort((a, b) => a - b);
const legacyCalls = featureCount * iterations;
const output = {
  featureCount,
  iterations,
  legacyEstimatedCadCalls: legacyCalls,
  optimizedCadCalls: cadCalls,
  callReductionPercent: Number(((1 - cadCalls / legacyCalls) * 100).toFixed(2)),
  orchestrationP50Ms: Number(samples[Math.floor(samples.length / 2)].toFixed(2)),
  orchestrationP95Ms: Number(samples[Math.floor(samples.length * 0.95)].toFixed(2)),
};
process.stdout.write(`${JSON.stringify(output, null, 2)}\n`);
