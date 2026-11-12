import { mkdirSync, writeFileSync } from "node:fs";
import { dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { network } from "hardhat";

// The web app reads this file as a configuration override (A web alkalmazás ezt a fájlt konfigurációs felülírásként olvassa)
const deploymentFile = fileURLToPath(new URL("../deployments/localhost.json", import.meta.url));

async function waitForNode(connection: Awaited<ReturnType<typeof network.connect>>) {
  // The node process starts before its JSON-RPC port accepts connections (A node processz előbb indul, mint ahogy a JSON-RPC port fogadja a kapcsolatokat)
  for (let attempt = 1; attempt <= 60; attempt++) {
    try {
      await connection.ethers.provider.getBlockNumber();
      return;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 1000));
    }
  }
  throw new Error("A Hardhat node nem válaszol a 127.0.0.1:8545 címen.");
}

async function main() {
  // A `npx hardhat node` által indított lokális lánc (http://127.0.0.1:8545)
  const connection = await network.connect("localhost");
  await waitForNode(connection);

  const verifier = await connection.ethers.deployContract("MockStarkVerifier");
  await verifier.waitForDeployment();
  console.log(`MockStarkVerifier címe: ${verifier.target}`);

  const proofOfSolvency = await connection.ethers.deployContract("ProofOfSolvency", [verifier.target]);
  await proofOfSolvency.waitForDeployment();
  console.log(`Okosszerződés címe (Hardhat:ContractAddress): ${proofOfSolvency.target}`);

  mkdirSync(dirname(deploymentFile), { recursive: true });
  writeFileSync(
    deploymentFile,
    JSON.stringify({ Hardhat: { ContractAddress: proofOfSolvency.target } }, null, 2) + "\n",
  );
  console.log(`Cím mentve: ${deploymentFile}`);
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
