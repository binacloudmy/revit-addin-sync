using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RevitWebAppSync
{
    public partial class AIAssistantWindow : Window
    {
        private const string PlaceholderText = "Type your question here...";

        public AIAssistantWindow()
        {
            InitializeComponent();
        }

        private void MessageInputBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (MessageInputBox.Text == PlaceholderText && MessageInputBox.Foreground == Brushes.Gray)
            {
                MessageInputBox.Text = "";
                MessageInputBox.Foreground = Brushes.Black;
            }
        }

        private void MessageInputBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MessageInputBox.Text))
            {
                MessageInputBox.Text = PlaceholderText;
                MessageInputBox.Foreground = Brushes.Gray;
            }
        }

        private void MessageInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                SendMessage();
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendMessage();
        }

        private async Task SendMessage()
        {
            string userMessage = MessageInputBox.Text?.Trim();

            if (string.IsNullOrEmpty(userMessage) || userMessage == PlaceholderText)
            {
                return;
            }

            // Add user message to chat
            AddUserMessage(userMessage);

            // Clear input and disable controls
            MessageInputBox.Text = PlaceholderText;
            MessageInputBox.Foreground = Brushes.Gray;
            SendButton.IsEnabled = false;
            MessageInputBox.IsEnabled = false;
            SendButton.Content = "Sending...";

            try
            {
                // Check if this requires multi-step response
                if (IsMultiStepResponse(userMessage))
                {
                    await HandleMultiStepResponse(userMessage);
                }
                else
                {
                    // Handle single response (existing behavior)
                    await HandleSingleResponse(userMessage);
                }

                // Auto-scroll to bottom
                ChatScrollViewer.ScrollToBottom();
            }
            catch (Exception ex)
            {
                AddAIResponse($"❌ Error processing request: {ex.Message}");
            }
            finally
            {
                // Re-enable controls
                SendButton.IsEnabled = true;
                MessageInputBox.IsEnabled = true;
                SendButton.Content = "Send";
            }
        }

        private bool IsMultiStepResponse(string userMessage)
        {
            string lowerMessage = userMessage.ToLower();

            // Check if this is the cafeteria rename request that needs multi-step flow
            if (lowerMessage.Contains("in floor plan 01") && lowerMessage.Contains("change the cafeteria name to dining area"))
            {
                return true;
            }

            // Check if this is the roof removal request that needs multi-step flow
            if (lowerMessage.Contains("can you remove the roof from this structure"))
            {
                return true;
            }

            return false;
        }

        private async Task HandleSingleResponse(string userMessage)
        {
            // Show typing indicator
            var typingIndicator = AddTypingIndicator();

            // Simulate AI processing delay
            await Task.Delay(7000);

            // Remove typing indicator
            ChatHistory.Children.Remove(typingIndicator);

            // Generate AI response based on user input
            string aiResponse = GenerateAIResponse(userMessage);
            AddAIResponse(aiResponse);
        }

        private async Task HandleMultiStepResponse(string userMessage)
        {
            string lowerMessage = userMessage.ToLower();

            // Handle cafeteria renaming multi-step flow
            if (lowerMessage.Contains("in floor plan 01") && lowerMessage.Contains("change the cafeteria name to dining area"))
            {
                // Step 1: Thinking
                AddAIResponse("thinking...");
                ChatScrollViewer.ScrollToBottom();
                await Task.Delay(2000);

                // Step 2: Analysis
                AddAIResponse("I see that there is a Cafeteria Section in Floor Plan: 01 - Entry Level, let me change the name to Dining Area");
                ChatScrollViewer.ScrollToBottom();
                await Task.Delay(3000);

                // Step 3: Executing
                AddAIResponse("executing...");
                ChatScrollViewer.ScrollToBottom();
                await Task.Delay(3000);

                // Step 4: Completion
                AddAIResponse("I've successfully changed the name Cafeteria to Dining Area as you requested.");
                ChatScrollViewer.ScrollToBottom();
            }
            // Handle roof removal multi-step flow
            else if (lowerMessage.Contains("can you remove the roof from this structure"))
            {
                // Step 1: Analyzing
                AddAIResponse("analyzing structure...");
                ChatScrollViewer.ScrollToBottom();
                await Task.Delay(2000);

                // Step 2: Structure Assessment
                AddAIResponse("I can see the roof structure in the current model. Let me proceed to remove it for you.");
                ChatScrollViewer.ScrollToBottom();
                await Task.Delay(3000);

                // Step 3: Removing
                AddAIResponse("removing roof elements...");
                ChatScrollViewer.ScrollToBottom();
                await Task.Delay(3000);

                // Step 4: Completion
                AddAIResponse("✅ I have successfully removed the roof from the structure as you requested.");
                ChatScrollViewer.ScrollToBottom();
            }
        }

        private void AddUserMessage(string message)
        {
            var userMessageBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(25, 118, 210)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(50, 0, 0, 10),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 400
            };

            var userMessageText = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            };

            userMessageBorder.Child = userMessageText;
            ChatHistory.Children.Add(userMessageBorder);

            ChatScrollViewer.ScrollToBottom();
        }

        private Border AddTypingIndicator()
        {
            var typingBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 249, 250)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(233, 236, 239)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 50, 10),
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 500
            };

            var typingPanel = new StackPanel();

            var aiLabel = new TextBlock
            {
                Text = "🤖 AI Assistant",
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(25, 118, 210)),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var typingText = new TextBlock
            {
                Text = "Typing...",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(108, 117, 125)),
                FontStyle = FontStyles.Italic
            };

            typingPanel.Children.Add(aiLabel);
            typingPanel.Children.Add(typingText);
            typingBorder.Child = typingPanel;

            ChatHistory.Children.Add(typingBorder);
            ChatScrollViewer.ScrollToBottom();

            return typingBorder;
        }

        private void AddAIResponse(string message)
        {
            var aiBorder = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 249, 250)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(233, 236, 239)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(15),
                Margin = new Thickness(0, 0, 50, 15),
                HorizontalAlignment = HorizontalAlignment.Left,
                MaxWidth = 500
            };

            var aiPanel = new StackPanel();

            var aiLabel = new TextBlock
            {
                Text = "🤖 AI Assistant",
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(25, 118, 210)),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var aiMessageText = new TextBlock
            {
                Text = message,
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(73, 80, 87)),
                TextWrapping = TextWrapping.Wrap
            };

            aiPanel.Children.Add(aiLabel);
            aiPanel.Children.Add(aiMessageText);
            aiBorder.Child = aiPanel;

            ChatHistory.Children.Add(aiBorder);
            ChatScrollViewer.ScrollToBottom();
        }

        private string GenerateAIResponse(string userMessage)
        {
            string lowerMessage = userMessage.ToLower();

            // Check if the user is asking who has the biggest d
            if (lowerMessage.Contains("who has the biggest d"))
            {
                return "Ammar has the biggest zettalodon";
            }
            // Check if the user is asking who has the smallest d
            else if (lowerMessage.Contains("who has the smallest d") || lowerMessage.Contains("who ha the smallest d"))
            {
                return "Acap has the smallest nanolodon";
            }
            // Check if the user is asking what structure this is
            else if (lowerMessage.Contains("what structure is this"))
            {
                return "This is a revit file named Technical School Current";
            }
            // Check if the user is asking for a brief description about the file
            else if (lowerMessage.Contains("can you give me a brief description about this file"))
            {
                return "Project Structure:\n\nThis appears to be a comprehensive educational building model with a well-organized hierarchy typical of institutional architecture projects.\n\nKey Components:\n\n• Floor Plans & Ceiling Plans - Standard architectural layouts showing room arrangements and overhead ceiling systems\n\n• 3D Views & Elevations - Three-dimensional perspectives and exterior building faces for design visualization\n\n• Sections - Both building sections (showing interior vertical cuts) and wall sections (detailed construction assemblies)\n\n• Detail Views - Close-up construction details for specific building components\n\n• Renderings - Photorealistic visualizations for presentation purposes\n\n• Drafting Views - 2D technical drawings and annotations\n\nDocumentation Elements:\n\n• Schedules/Quantities - Automated lists of building components, materials, and quantities for cost estimation and construction\n\n• Sheets - Organized drawing sets ready for printing and construction documentation\n\n• Legends - Symbol explanations and drawing standards\n\n• Area Plans - Space calculations for building programming and code compliance";
            }
            else
            {
                return "Entah la sir cuba tanya Kamil";
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }
    }
}