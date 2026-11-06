import { network } from "hardhat";

async function main() {
  // A `npx hardhat node` által indított lokális lánc (http://127.0.0.1:8545)
  const connection = await network.connect("localhost");

  const verifier = await connection.ethers.deployContract("MockStarkVerifier");
  await verifier.waitForDeployment();
  console.log(`MockStarkVerifier címe: ${verifier.target}`);

  const proofOfSolvency = await connection.ethers.deployContract("ProofOfSolvency", [verifier.target]);
  await proofOfSolvency.waitForDeployment();
  console.log(`Okosszerződés címe (Hardhat:ContractAddress): ${proofOfSolvency.target}`);
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
