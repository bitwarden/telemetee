//! Verifies a collector's attestation and sends telemetry pages against its channel.
#![allow(clippy::print_stdout, clippy::print_stderr)]

use std::process::ExitCode;

use clap::{Parser, Subcommand};
use telemetee_client::{
    CollectorClient, attestation::AttestationPolicy, models::TelemetryRecord, page::Submission,
};
use url::Url;

#[derive(Parser)]
#[command(about, version)]
struct Cli {
    /// Collector origin, for example http://localhost:5000
    #[arg(long)]
    base_url: Url,

    /// Exact collector version this client was built against; sent in the Telemetee-Target-Version header
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
    /// Pack one or more records into a page and POST it to /telemetry.
    ///
    /// Each record is a flat JSON object of the form
    /// `{"schema": "<name>", "<field>": <value>, ...}`.
    Send {
        /// JSON record. Repeat for multiple records in the same send.
        #[arg(long = "record", required = true)]
        records: Vec<String>,
        /// Optional JWT. When set, every --record is submitted as an authed record under this
        /// token. When unset, every --record is submitted anonymously.
        #[arg(long)]
        auth: Option<String>,
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

    match cli.command {
        Command::Attestation => match client.attestation().await {
            Ok(attestation) => {
                println!(
                    "{}",
                    serde_json::to_string_pretty(&attestation.claims).unwrap_or_default()
                );
                ExitCode::SUCCESS
            }
            Err(error) => fail(error),
        },
        Command::Send { records, auth } => match send(&client, records, auth).await {
            Ok(()) => ExitCode::SUCCESS,
            Err(error) => fail(error),
        },
    }
}

async fn send(
    client: &CollectorClient,
    records: Vec<String>,
    auth: Option<String>,
) -> Result<(), telemetee_client::Error> {
    // JSON on the command line is deserialized straight into the generated TelemetryRecord enum.
    // Unknown schema values or missing required fields fail here, before any crypto work.
    let mut submissions = Vec::with_capacity(records.len());
    for raw in records {
        let event = serde_json::from_str::<TelemetryRecord>(&raw)?;
        submissions.push(match &auth {
            Some(token) => Submission::Authed {
                token: token.clone(),
                event,
            },
            None => Submission::Unauthed(event),
        });
    }
    let outcome = client.send(&mut submissions).await?;
    eprintln!(
        "sent {} record(s) in {} page(s); {} dropped as oversize",
        outcome.records_sent,
        outcome.pages_sent,
        outcome.oversize_dropped.len()
    );
    if !submissions.is_empty() {
        eprintln!(
            "{} record(s) remain in the queue after send",
            submissions.len()
        );
    }
    Ok(())
}

fn fail(error: telemetee_client::Error) -> ExitCode {
    eprintln!("error: {error}");
    ExitCode::FAILURE
}
