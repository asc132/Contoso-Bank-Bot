﻿using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Web.Http;
using System.Web.Http.Description;
using Microsoft.Bot.Connector;
using Newtonsoft.Json;
using System.Collections.Generic;
using Contoso_Bank_Bot.Models;
using System.Globalization;

namespace Contoso_Bank_Bot
{
    [BotAuthentication]
    public class MessagesController : ApiController
    {
        /// <summary>
        /// POST: api/Messages
        /// Receive a message from a user and reply to it
        /// </summary>

        private static async Task<BankingLUIS.RootObject> GetEntityFromLUIS(string Query)
        {
            Query = Uri.EscapeDataString(Query);
            BankingLUIS.RootObject Data = new BankingLUIS.RootObject();
            using (HttpClient client = new HttpClient())
            {
                string RequestURI = "https://api.projectoxford.ai/luis/v2.0/apps/98adb48c-8501-4942-979a-f08cc5e2c613?subscription-key=6b1bc83ea6844b47b4dbf218444b163d&q=" + Query + "&verbose=true";
                HttpResponseMessage msg = await client.GetAsync(RequestURI);

                if (msg.IsSuccessStatusCode)
                {
                    var JsonDataResponse = await msg.Content.ReadAsStringAsync();
                    Data = JsonConvert.DeserializeObject<BankingLUIS.RootObject>(JsonDataResponse);
                }
            }
            return Data;
        }

        private async Task<string> GetConversion(HttpClient Client, BankingLUIS.RootObject BkLUIS)
        {
            string Currency1 = null;
            string Currency2 = null;
            double Amount = 1;
            if (BkLUIS.entities.Count() > 2)
            {
                Amount = Convert.ToDouble(BkLUIS.entities[0].entity);
                Currency1 = BkLUIS.entities[1].entity;
                Currency2 = BkLUIS.entities[2].entity;                
            }
            else
            {
                Currency1 = BkLUIS.entities[0].entity;
                Currency2 = BkLUIS.entities[1].entity;
            }          
            double? rate = await Fixer.ConversionAsync(Client, Currency1, Currency2);
            if (rate == null)
            {
                return string.Format("This \"{0}\" is not an valid currency", Currency1);
            }
            else
            {                
                return Amount + " " + Currency1.ToUpper() + " is equivalent to " + (Amount * rate) + " " + Currency2.ToUpper();
            }
        }

        private async Task<string> Greeting(BotData userData, StateClient stateClient, Activity activity)
        {
            string outputString = "Hello";

            if (userData.GetProperty<bool>("SentGreeting"))
            {
                if (userData.GetProperty<User>("User") != null)
                {
                    outputString = "Hello, " + userData.GetProperty<User>("User").Name;
                }
                else
                {
                    outputString = "Hello again";
                }                
            }
            else          
            {
                userData.SetProperty<bool>("SentGreeting", true);
                await stateClient.BotState.SetUserDataAsync(activity.ChannelId, activity.From.Id, userData);
            }
            return outputString;
        }

        private string GetBalance(BotData userData, BankingLUIS.RootObject BkLUIS)
        {
            string Account = BkLUIS.entities[0].entity.ToLower();
            string outputString;
            User user = userData.GetProperty<User>("User");
            if (user != null)
            {
                double balance;
                switch (Account)
                {
                    case "checking":
                    case "cheque":
                        balance = user.Cheque;
                        break;
                    case "savings":
                        balance = user.Savings;
                        break;
                    default:                        
                        return "Invalid account type";
                }
                outputString = "You have " + balance + " " + userData.GetProperty<User>("User").Currency + " in your " + Account + " account";
            }
            else
            {
                outputString = "You must log in to access accounts";
            }
            return  outputString;
        }

        private async Task<string> Transfer(BotData userData, BankingLUIS.RootObject BkLUIS, StateClient stateClient, Activity activity)
        {
            double Number = Convert.ToDouble(BkLUIS.entities[0].entity);
            string Account1 = BkLUIS.entities[1].entity.ToLower();
            string Account2 = BkLUIS.entities[2].entity.ToLower();
            string outputString;

            User user = userData.GetProperty<User>("User");
            
            if (user != null)
            {
                double cheque = user.Cheque;
                double savings = user.Savings;

                switch (Account1)
                {
                    case "cheque":
                    case "checking":
                        cheque -= Number;
                        savings += Number;
                        break;
                    case "savings":
                        cheque += Number;
                        savings -= Number;
                        break;
                    default:
                        return "Invalid account type";                        
                }
                User temp = new User()
                {
                    ID = user.ID,
                    Name = user.Name,
                    Cheque = cheque,
                    Savings = savings,
                    Date = user.Date,
                    Currency = user.Currency
                  
                };
                userData.SetProperty<User>("User", temp);
                await stateClient.BotState.SetUserDataAsync(activity.ChannelId, activity.From.Id, userData);
                await AzureManager.AzureManagerInstance.UpdateUser(temp);
                outputString = Number + " has been transfered from your " + Account1 + " account to your " + Account2 + " account";
            }
            else
            {
                outputString = "You must log in to access accounts";
            }
            return outputString;       
        }

        private async Task<string> CreateUser(BankingLUIS.RootObject BkLUIS)
        {
            
            TextInfo textInfo = new CultureInfo("en-US", false).TextInfo;            
            User user = new User()
            {
                ID = BkLUIS.entities[2].entity,
                Name = textInfo.ToTitleCase(BkLUIS.entities[1].entity),
                Savings = Convert.ToDouble(BkLUIS.entities[0].entity),
                Cheque = Convert.ToDouble(BkLUIS.entities[6].entity),
                Currency = BkLUIS.entities[7].entity.ToUpper()
            };
            try
            {
                await AzureManager.AzureManagerInstance.AddUser(user);
            }
            catch
            {
                return "User already exists";
            }
            
            string outputString = "User " + user.Name + " added with ID " + user.ID;
            return outputString;
            }

        private async Task<string> DeleteUser(BankingLUIS.RootObject BkLUIS, StateClient stateClient, Activity activity)
        {
            string outputString = "User not found";
            List<User> users = await AzureManager.AzureManagerInstance.GetUser();
            foreach (User t in users)
            {
                if (t.ID.Equals(BkLUIS.entities[0].entity))
                {
                    User user = t;
                    await AzureManager.AzureManagerInstance.DeleteUser(t);
                    outputString = user.Name + ", with ID "+ BkLUIS.entities[0].entity + ", has been deleted";
                    await stateClient.BotState.DeleteStateForUserAsync(activity.ChannelId, activity.From.Id);
                }
            }
            return outputString;
        }

        public async Task<HttpResponseMessage> Post([FromBody]Activity activity)
        {
            if (activity.Type == ActivityTypes.Message)
            {
                ConnectorClient connector = new ConnectorClient(new Uri(activity.ServiceUrl));

                StateClient stateClient = activity.GetStateClient();
                BotData userData = await stateClient.BotState.GetUserDataAsync(activity.ChannelId, activity.From.Id);

                string outputString = "ERROR";
                bool LUISMessage = true;
                var userMessage = activity.Text;

                if (userMessage.Length > 9)
                {
                    if (userMessage.ToLower().Substring(0, 8).Equals("set user"))
                    {
                        string id = userMessage.Substring(9);
                        List<User> users = await AzureManager.AzureManagerInstance.GetUser();
                        outputString = "No user found";
                        foreach (User t in users)
                        {
                            if (t.ID.Equals(id))
                            {
                                userData.SetProperty<User>("User", t);
                                outputString = userData.GetProperty<User>("User").Name + " is now logged in";
                                await stateClient.BotState.SetUserDataAsync(activity.ChannelId, activity.From.Id, userData);
                            }
                        }
                        LUISMessage = false;
                    }
                }
                if (userMessage.ToLower().Equals("help"))
                {                    
                    outputString = "Things you can without being logged in\n\n";
                    outputString += "To log in:\n\n";
                    outputString += "Type 'set user <8_DIGIT_ID>'\n\n";                    
                    outputString += "To get currency conversion rates:\n\n";
                    outputString += "Type something like '30 USD to NZD'\n\n";                    
                    outputString += "For information on Contoso Bank:\n\n";
                    outputString += "Type 'about'";
                    
                    Activity infoReply = activity.CreateReply(outputString);
                    await connector.Conversations.ReplyToActivityAsync(infoReply);
                    outputString = "Things you can do logged in\n\n";
                    outputString += "To log out:\n\n";
                    outputString += "Type 'log out'\n\n";
                    outputString += "To get an account balance:\n\n";
                    outputString += "Type 'balance savings' or 'what's in my checking'\n\n";
                    outputString += "To transfer money between accounts:\n\n";
                    outputString += "Type something like 'transfer 40 from savings to checking'";

                    Activity infoReply2 = activity.CreateReply(outputString);
                    await connector.Conversations.ReplyToActivityAsync(infoReply2);
                    outputString = "For adding and removing users\n\n";
                    outputString += "To create a user:\n\n";
                    outputString += "Type 'create user <8_DIGIT_ID>, <NAME>, s:<SAVINGS_AMOUNT>, c:<CHECKING_AMOUNT>, <CURRENCY>'\n\n";
                    outputString += "To delete a user:\n\n";
                    outputString += "Type 'delete user <8_DIGIT_ID>'";

                    LUISMessage = false;
                }                
                if (userMessage.ToLower().Equals("user"))
                {
                    User user = userData.GetProperty<User>("User");
                    if (user == null)
                    {
                        outputString = "Not logged in";
                    }
                    else
                    {
                        outputString = user.Name + " is currently logged in";
                    }
                    LUISMessage = false;
                }
                if (userMessage.ToLower().Equals("log out"))
                {
                    outputString = "Looged Out. User Data Cleared";
                    await stateClient.BotState.DeleteStateForUserAsync(activity.ChannelId, activity.From.Id);
                    LUISMessage = false;
                }
                if (userMessage.ToLower().Equals("about"))
                {
                    Activity about = activity.CreateReply($"About Contoso Bank");
                    about.Recipient = activity.From;
                    about.Type = "message";
                    about.Attachments = new List<Attachment>();

                    List<CardImage> cardImages = new List<CardImage>();

                    cardImages.Add(new CardImage(url: "https://s17.postimg.org/wvo2cfnvz/Logomakr_3d_FXZy.png"));

                    List<CardAction> cardButtons = new List<CardAction>();                   
                    CardAction plButton = new CardAction()
                    {
                        Value = "https://www.google.co.nz/search?q=contoso%20bank",
                        Type = "openUrl",
                        Title = "Search for Contoso Bank"
                    };
                    cardButtons.Add(plButton);

                    ThumbnailCard plCard = new ThumbnailCard()
                    {
                        Title = "Contoso Bank",
                        Subtitle = "Contact us or search for us online\n\n Phone No: 03 555 5555\n\n",
                        Images = cardImages,
                        Buttons = cardButtons
                    };

                    Attachment plAttachment = plCard.ToAttachment();
                    about.Attachments.Add(plAttachment);
                    await connector.Conversations.SendToConversationAsync(about);

                }
                else if (userMessage.ToLower().Equals("ollie robb"))
                {
                    Activity about = activity.CreateReply($"Look at this man");
                    about.Recipient = activity.From;
                    about.Type = "message";
                    about.Attachments = new List<Attachment>();

                    List<CardImage> cardImages = new List<CardImage>();

                    cardImages.Add(new CardImage(url: "https://scontent-syd2-1.xx.fbcdn.net/v/t1.0-9/11986465_10207896272872723_8728011847257622060_n.jpg?oh=9b44fd3afd1259b8fbf65b9f18d25d79&oe=58F7FAAC"));

                    List<CardAction> cardButtons = new List<CardAction>();
                    CardAction plButton = new CardAction()
                    {
                        Value = "https://www.facebook.com/osrobb",
                        Type = "openUrl",
                        Title = "Check this bad boy out"
                    };
                    cardButtons.Add(plButton);

                    HeroCard plCard = new HeroCard()
                    {
                        Title = "Ollie Robb",
                        Subtitle = "So Aesthetic",
                        Images = cardImages,
                        Buttons = cardButtons
                    };

                    Attachment plAttachment = plCard.ToAttachment();
                    about.Attachments.Add(plAttachment);
                    await connector.Conversations.SendToConversationAsync(about);

                }
                else if (LUISMessage)
                {
                    HttpClient client = new HttpClient();

                    BankingLUIS.RootObject BkLUIS = await GetEntityFromLUIS(activity.Text);
                    if (BkLUIS.intents.Count() > 0)
                    {
                        switch (BkLUIS.intents[0].intent)
                        {
                            case "NewUser":
                                outputString = await CreateUser(BkLUIS);
                                break;
                            case "DeleteUser":
                                outputString = await DeleteUser(BkLUIS, stateClient, activity);
                                break;
                            case "Transfer":
                                outputString = await Transfer(userData, BkLUIS, stateClient, activity);
                                break;
                            case "GetBalance":
                                outputString = GetBalance(userData, BkLUIS);
                                break;
                            case "Hello":
                                outputString = await Greeting(userData, stateClient, activity);
                                break;
                            case "Goodbye":
                                outputString = "Goodbye!";
                                break;
                            case "GetRates":
                                outputString = await GetConversion(client, BkLUIS);
                                break;
                            default:
                                outputString = "Sorry, I don't quite understand...";
                                break;
                        }
                    }
                    else
                    {
                        outputString = "Sorry, I don't quite understand...";
                    }
                    Activity infoReply = activity.CreateReply(outputString);
                    await connector.Conversations.ReplyToActivityAsync(infoReply);
                }              
                else
                {
                    Activity infoReply = activity.CreateReply(outputString);
                    await connector.Conversations.ReplyToActivityAsync(infoReply);
                }

               

            }
            else
            {
                HandleSystemMessage(activity);
            }
            var response = Request.CreateResponse(HttpStatusCode.OK);
            return response;
        }

        private Activity HandleSystemMessage(Activity message)
        {
            if (message.Type == ActivityTypes.DeleteUserData)
            {
                // Implement user deletion here
                // If we handle user deletion, return a real message
            }
            else if (message.Type == ActivityTypes.ConversationUpdate)
            {
                // Handle conversation state changes, like members being added and removed
                // Use Activity.MembersAdded and Activity.MembersRemoved and Activity.Action for info
                // Not available in all channels
                string replyMessage = string.Empty;
                replyMessage += $"Hello, and welcome to the Contoso Bank Bot\n\n";
                replyMessage += $"This bot can help you fufil tasks without going into your closest bank.  \n";
                replyMessage += $"Possible tasks include:  \n";
                replyMessage += $"* Transfering funds between accounts\n\n";
                replyMessage += $"* Getting a conversion rate for 2 currencies\n\n";
                replyMessage += $"* Getting an accounts balance\n\n";
                replyMessage += $"For more assistance please type 'help'\n\n.";
                replyMessage += $"Thank you for choosing Contoso Bank";
                return message.CreateReply(replyMessage);
            }
            else if (message.Type == ActivityTypes.ContactRelationUpdate)
            {
                // Handle add/remove from contact lists
                // Activity.From + Activity.Action represent what happened
            }
            else if (message.Type == ActivityTypes.Typing)
            {
                // Handle knowing tha the user is typing
            }
            else if (message.Type == ActivityTypes.Ping)
            {
            }

            return null;
        }
    }
}