// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.SemanticKernel;
using Agent365SemanticKernelSampleAgent.Agents;
using System.ComponentModel;
using System.Threading.Tasks;
using System;

namespace Agent365SemanticKernelSampleAgent.Plugins;

/// <summary>
/// Budget & Finance Plugin - Specialized tools for email communication and financial management
/// MCP Servers: Microsoft Outlook Mail, Microsoft SharePoint/OneDrive (read-only), Microsoft Teams
/// </summary>
public class BudgetFinancePlugin
{
    #region Microsoft Outlook Mail Functions

    [KernelFunction("send_email_to_vendor"), Description("Send an email to a vendor for quotes, confirmations, or payment discussions")]
    public Task<string> SendEmailToVendorAsync(
        [Description("Vendor email address")] string vendorEmail,
        [Description("Subject of the email")] string subject,
        [Description("Body content of the email")] string body)
    {
        var timestamp = DateTime.Now.ToString("MM/dd/yyyy h:mm tt");
        return Task.FromResult($@"[BUDGET & FINANCE] ✓ Email sent successfully

To: {vendorEmail}
Subject: {subject}
Sent: {timestamp}

Message preview:
{body}

Status: Delivered
The vendor should respond within 24-48 hours.");
    }

    [KernelFunction("get_vendor_emails"), Description("Retrieve emails from vendors with quotes and pricing information")]
    public Task<string> GetVendorEmailsAsync(
        [Description("Optional: Filter by vendor name or subject keyword")] string? filterKeyword = null)
    {
        // Mock implementation showing typical vendor responses
        var filter = string.IsNullOrEmpty(filterKeyword) ? "all vendors" : $"containing '{filterKeyword}'";
        return Task.FromResult($@"[BUDGET & FINANCE] ✓ Retrieved vendor emails ({filter}):

1. From: venue@premiumevents.com
   Subject: Re: Quote Request - Q2 All-Hands Venue
   Date: Today, 11:30 AM
   Quote: $12,000 for 150 people, includes A/V setup
   
2. From: catering@deluxefood.com
   Subject: Catering Quote - Corporate Event
   Date: Today, 10:45 AM
   Quote: €15,000 (approximately $16,350 USD)
   
3. From: tech@avstudio.com
   Subject: A/V Equipment Quote
   Date: Yesterday, 4:20 PM
   Quote: $3,500 for full setup and technician

Total vendor responses: 3");
    }

    [KernelFunction("send_email_to_finance_team"), Description("Send an email to the internal finance team for budget approval or payment requests")]
    public Task<string> SendEmailToFinanceTeamAsync(
        [Description("Subject of the email")] string subject,
        [Description("Email body with budget details")] string body,
        [Description("Amount requesting approval for")] decimal amount)
    {
        var timestamp = DateTime.Now.ToString("MM/dd/yyyy h:mm tt");
        return Task.FromResult($@"[BUDGET & FINANCE] ✓ Email sent to Finance Department

To: finance-team@company.com
Subject: {subject}
Amount: ${amount:N2}
Sent: {timestamp}

Message:
{body}

Status: Awaiting approval (typical response time: 2-4 hours)
You will be notified when finance team responds.");
    }

    [KernelFunction("send_payment_confirmation"), Description("Send payment confirmation email to vendor")]
    public Task<string> SendPaymentConfirmationAsync(
        [Description("Vendor email address")] string vendorEmail,
        [Description("Payment amount")] decimal amount,
        [Description("Invoice or reference number")] string referenceNumber)
    {
        var timestamp = DateTime.Now.ToString("MM/dd/yyyy h:mm tt");
        var body = $@"Dear Vendor,

This email confirms that payment of ${amount:N2} has been processed.

Reference Number: {referenceNumber}
Payment Date: {timestamp}
Payment Method: Wire Transfer

Please allow 3-5 business days for funds to appear in your account.

Thank you for your service.

Best regards,
Budget & Finance Team";

        return Task.FromResult($@"[BUDGET & FINANCE] ✓ Payment confirmation sent

To: {vendorEmail}
Subject: Payment Confirmation - {referenceNumber}
Amount: ${amount:N2}
Sent: {timestamp}

Message:
{body}");
    }

    #endregion

    #region Currency Conversion Functions

    [KernelFunction("convert_currency"), Description("Convert currency amounts for international vendor quotes")]
    public Task<string> ConvertCurrencyAsync(
        [Description("Amount to convert")] decimal amount,
        [Description("Source currency code (e.g., EUR, GBP, JPY)")] string fromCurrency,
        [Description("Target currency code (e.g., USD)")] string toCurrency)
    {
        // Mock conversion rates (in production, would call real exchange rate API)
        var exchangeRates = new System.Collections.Generic.Dictionary<string, decimal>
        {
            { "EUR_USD", 1.09m },
            { "GBP_USD", 1.27m },
            { "JPY_USD", 0.0071m },
            { "CAD_USD", 0.74m },
            { "AUD_USD", 0.66m }
        };

        var rateKey = $"{fromCurrency}_{toCurrency}";
        var rate = exchangeRates.ContainsKey(rateKey) ? exchangeRates[rateKey] : 1.0m;
        var convertedAmount = amount * rate;

        return Task.FromResult($@"[BUDGET & FINANCE] ✓ Currency conversion

Original: {amount:N2} {fromCurrency}
Converted: {convertedAmount:N2} {toCurrency}
Exchange Rate: 1 {fromCurrency} = {rate:N4} {toCurrency}
Rate Date: {DateTime.Now:MM/dd/yyyy}

Note: Rates are approximate and subject to change at time of transaction.");
    }

    #endregion

    #region Microsoft SharePoint Functions (Read-Only)

    [KernelFunction("read_budget_documents"), Description("Read budget and financial documents from SharePoint (read-only access)")]
    public Task<string> ReadBudgetDocumentsAsync(
        [Description("Folder path or document name to read")] string documentPath)
    {
        // Mock implementation
        return Task.FromResult($@"[BUDGET & FINANCE] ✓ Retrieved document from SharePoint: '{documentPath}'

BUDGET SUMMARY:
- Venue: $12,000
- Catering: $16,350 (converted from €15,000)
- A/V Equipment: $3,500
- Miscellaneous: $2,150
- Contingency (10%): $3,400
-----------------------------------
TOTAL ESTIMATED: $37,400
APPROVED BUDGET: $45,000
REMAINING: $7,600

Status: Within budget ✓");
    }

    [KernelFunction("access_sharepoint_file"), Description("Access a specific file from SharePoint for review")]
    public Task<string> AccessSharePointFileAsync(
        [Description("Full path to the file")] string filePath)
    {
        return Task.FromResult($"[BUDGET & FINANCE] ✓ Accessed SharePoint file: '{filePath}'\n\n  Permission: Read-Only\n  Note: For editing documents, contact the Event Coordinator agent");
    }

    #endregion

    #region Microsoft Teams Functions

    [KernelFunction("post_budget_update"), Description("Post a budget update or financial status to Microsoft Teams")]
    public Task<string> PostBudgetUpdateAsync(
        [Description("The channel name")] string channelName,
        [Description("Budget update message")] string message)
    {
        var timestamp = DateTime.Now.ToString("h:mm tt");
        return Task.FromResult($"[BUDGET & FINANCE] ✓ Posted to Teams channel '{channelName}' at {timestamp}:\n\n  💰 {message}\n\n  Team notified of financial status");
    }

    [KernelFunction("read_teams_messages"), Description("Read messages from Microsoft Teams channel")]
    public Task<string> ReadTeamsMessagesAsync(
        [Description("The channel name to read from")] string channelName)
    {
        return Task.FromResult($@"[BUDGET & FINANCE] ✓ Recent messages in '{channelName}':

1. Event Coordinator (15 mins ago): 📄 Venue options uploaded to SharePoint
2. Logistics Scheduler (30 mins ago): 📅 Need budget approval to proceed
3. Budget & Finance (1 hour ago): 💰 Requesting quotes from 3 vendors
4. Manager (2 hours ago): Please keep event under $45,000

Total: 4 recent messages");
    }

    #endregion

    #region Agent Orchestration Functions

    [KernelFunction("analyze_remaining_tasks"), Description("Analyze what budget and financial tasks remain to be completed")]
    public Task<string> AnalyzeRemainingTasksAsync(
        [Description("Current budget status")] string currentStatus)
    {
        return Task.FromResult($@"[BUDGET & FINANCE] ✓ Task Analysis Complete

COMPLETED TASKS:
✓ Vendor quote requests sent
✓ International currency converted
✓ Budget summary created
✓ Finance team approval obtained

REMAINING TASKS:
→ Schedule venue booking time (LOGISTICS SCHEDULER AGENT)
→ Schedule catering delivery (LOGISTICS SCHEDULER AGENT)
→ Schedule A/V setup (LOGISTICS SCHEDULER AGENT)
→ Send calendar invitations to attendees (LOGISTICS SCHEDULER AGENT)

NEXT ACTION REQUIRED:
Need Logistics Scheduler agent to:
1. Create calendar events for all activities
2. Schedule vendor access times
3. Send invitations to all 150 attendees

RECOMMENDED HANDOFF: Logistics Scheduler Agent");
    }

    [KernelFunction("handoff_to_logistics_scheduler"), Description("Hand off the event planning task to the Logistics Scheduler agent after budget approval is complete")]
    public Task<string> HandoffToLogisticsSchedulerAsync(
        [Description("Summary of budget work completed")] string completedWork,
        [Description("What the Logistics Scheduler agent needs to do")] string nextSteps)
    {
        var timestamp = DateTime.Now.ToString("MM/dd/yyyy h:mm tt");
        return Task.FromResult($@"[BUDGET & FINANCE] ✓ Handing off to Logistics Scheduler Agent

HANDOFF TIME: {timestamp} PST
FROM: Budget & Finance Agent
TO: Logistics Scheduler Agent

WORK COMPLETED:
{completedWork}

NEXT STEPS FOR LOGISTICS AGENT:
{nextSteps}

STATUS: Ready for handoff
BUDGET STATUS: Approved - $37,400 of $45,000 allocated
CONTEXT: Vendor confirmations sent, all payments approved

@LogisticsSchedulerAgent - You're up! Please proceed with scheduling all event activities.");
    }

    #endregion

    #region Helper Functions

    [KernelFunction("get_budget_finance_capabilities"), Description("Get a summary of what the Budget & Finance agent can do")]
    public Task<string> GetCapabilitiesAsync()
    {
        return Task.FromResult(@"[BUDGET & FINANCE] My specialized capabilities:

📧 EMAIL COMMUNICATION (Microsoft Outlook):
  - Send emails to vendors for quotes
  - Communicate with finance department
  - Send payment confirmations
  - Retrieve vendor responses

💱 FINANCIAL MANAGEMENT:
  - Convert international currency
  - Track budget and expenses
  - Process payment requests

📁 DOCUMENT ACCESS (SharePoint - Read Only):
  - Read budget documents
  - Access financial spreadsheets
  - Review vendor quotes

💬 TEAM UPDATES (Microsoft Teams):
  - Post budget status updates
  - Coordinate financial approvals

I specialize in vendor communication and financial management.
For document creation, ask the Event Coordinator agent.
For scheduling, ask the Logistics Scheduler agent.");
    }

    [KernelFunction("create_budget_summary"), Description("Create a summary of budget status")]
    public Task<string> CreateBudgetSummaryAsync(
        [Description("Total approved budget")] decimal approvedBudget,
        [Description("Comma-separated list of expenses with amounts (e.g., 'Venue:12000,Catering:16350')")] string expenses)
    {
        var expenseItems = expenses.Split(',');
        decimal totalSpent = 0;
        var breakdown = "EXPENSE BREAKDOWN:\n";

        foreach (var expense in expenseItems)
        {
            var parts = expense.Split(':');
            if (parts.Length == 2)
            {
                var category = parts[0].Trim();
                if (decimal.TryParse(parts[1].Trim(), out var amount))
                {
                    totalSpent += amount;
                    breakdown += $"  - {category}: ${amount:N2}\n";
                }
            }
        }

        var remaining = approvedBudget - totalSpent;
        var percentUsed = (totalSpent / approvedBudget) * 100;

        return Task.FromResult($@"[BUDGET & FINANCE] ✓ Budget Summary Created

{breakdown}
-----------------------------------
Total Spent: ${totalSpent:N2}
Approved Budget: ${approvedBudget:N2}
Remaining: ${remaining:N2}
Budget Used: {percentUsed:N1}%

Status: {(remaining >= 0 ? "✓ Within Budget" : "⚠ Over Budget")}");
    }

    #endregion
}
