var builder = DistributedApplication.CreateBuilder(args);

// The Rust zk-STARK prover (loopback :3000)
var prover = builder.AddExecutable("zk-prover", "cargo", Path.Combine(builder.AppHostDirectory, "..", "calc"), "run", "--release");

// The local Hardhat node (JSON-RPC on :8545)
var contractDirectory = Path.Combine(builder.AppHostDirectory, "..", "SolvencyProofContract");
var hardhat = builder.AddExecutable("hardhat-node", "npx", contractDirectory, "hardhat", "node");

// Deploys the contract to the node and writes its address for the web app
var deploy = builder.AddExecutable("hardhat-deploy", "npx", contractDirectory, "hardhat", "run", "scripts/deploy.ts", "--network", "localhost")
    .WaitFor(hardhat);

builder.AddProject<Projects.SolvencyProof>("web")
    .WaitFor(prover)
    .WaitFor(hardhat)
    .WaitForCompletion(deploy);

builder.Build().Run();
