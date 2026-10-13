//! Verifies a collector's attestation and exercises its Noise channel.
#![allow(clippy::print_stdout, clippy::print_stderr)]

use std::process::ExitCode;

use clap::{Parser, Subcommand};
use telemetee_client::{CollectorClient, attestation::AttestationPolicy};
use url::Url;

#[derive(Parser)]
#[command(about, version)]
struct Cli {
    /// Collector origin, for example http://localhost:5000
    #[arg(long)]
    base_url: Url,

    /// Exact collector version, the URL path segment the client pins
    #[arg(long)]
    collector_version: String,

    /// Pinned attestation issuer, for example https://sharedeus2.eus2.attest.azure.net
    #[arg(long)]
    issuer: Url,

    /// Pinned CCE policy hash (x-ms-sevsnpvm-hostdata), hex
    #[arg(long)]
    hostdata: String,

    #[command(subcommand)]
    command: Command,
}

#[derive(Subcommand)]
enum Command {
    /// Fetch and verify the attestation token, printing its claims
    Attestation,
    /// Send a message through the Noise channel and print what comes back
    Echo {
        /// Text to send
        message: String,
    },
}

#[tokio::main]
async fn main() -> ExitCode {
    let cli = Cli::parse();
    let policy = AttestationPolicy {
        issuer: cli.issuer,
        hostdata: cli.hostdata,
    };
    let client = match CollectorClient::new(cli.base_url, &cli.collector_version, policy) {
        Ok(client) => client,
        Err(error) => return fail(error),
    };

    let result = match cli.command {
        Command::Attestation => client.attestation().await.map(|attestation| {
            serde_json::to_string_pretty(&attestation.claims).unwrap_or_default()
        }),
        Command::Echo { message } => client
            .echo(message.as_bytes())
            .await
            .map(|reply| String::from_utf8_lossy(&reply).into_owned()),
    };

    match result {
        Ok(output) => {
            println!("{output}");
            ExitCode::SUCCESS
        }
        Err(error) => fail(error),
    }
}

fn fail(error: telemetee_client::Error) -> ExitCode {
    eprintln!("error: {error}");
    ExitCode::FAILURE
}
