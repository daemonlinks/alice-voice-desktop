"""Text used to synthesise training clips for the "hey alice" wake word."""

POSITIVE = [
    "hey alice", "hey alice.", "hey, alice", "hey alice!", "hey, alice.", "hey alice?",
    "Hey Alice.", "hey alice,",
]

# Near misses the model must learn to reject.
ADVERSARIAL = [
    "alice", "alice.", "hey", "hey.", "hey alex", "hey alexa", "hey ellis", "hey ali", "hey eli",
    "hey allison", "hey a list", "malice", "palace", "hey jarvis", "hey there", "hey all",
    "a lease", "hey you", "hey siri", "hello", "alice in wonderland", "hey annie", "hey elsie",
    "hey alan", "the police", "hey lettuce", "hey alison's dad", "hey alfie", "hey, I miss", "hey Elise",
    "a lisp", "hey Callis", "hey Lisa", "hey, a nice day", "hayley", "hey, Ally", "hey Alistair",
    "hey Alastair", "they all list", "hey ice", "Halice", "hey Celia", "hey Felix",
]

GENERIC = [
    "The weather today is cloudy with a chance of rain later this afternoon.",
    "Could you please put the kettle on and check the post.",
    "I think we should leave a bit earlier tomorrow morning.",
    "Turn the music down, it is far too loud in here.",
    "My train was delayed again by twenty minutes.",
    "What time does the shop close on Sundays?",
    "She bought three apples, a loaf of bread and some cheese.",
    "The meeting has been moved to half past two.",
    "Have you seen my keys anywhere? I left them on the table.",
    "Let's watch a film tonight after dinner.",
    "The cat is sleeping on the windowsill in the sun.",
    "Remind me to call my mother this evening.",
    "There is a lot of traffic on the motorway.",
    "He said the report would be ready by Friday.",
    "Please send the invoice to the accounts department.",
    "We are going to the seaside for the weekend.",
    "Can you read me the latest news headlines?",
    "This soup needs a little more salt and pepper.",
    "The children are playing football in the garden.",
    "I would like a cup of tea with milk, no sugar.",
    "Set a timer for ten minutes please.",
    "The library opens at nine o'clock every day.",
    "Our flight leaves from terminal five at noon.",
    "It was a lovely evening and the stars were out.",
    "Do you know where the nearest petrol station is?",
    "I have to finish this spreadsheet before lunch.",
    "The new computer is much faster than the old one.",
    "Hello everyone, thank you for joining the call.",
    "They painted the living room a pale shade of green.",
    "Alice and Bob went to the market on Saturday.",
    "Is it going to snow in Scotland this week?",
    "The garden needs watering because it has been so dry.",
    "Please add washing up liquid to the shopping list.",
    "I am not sure that is the right answer.",
    "The printer has run out of paper again.",
    "We should book a table at the Italian restaurant.",
    "Hey, how are you doing today?",
    "My favourite colour has always been blue.",
    "The bus stop is just around the corner.",
    "Could you switch off the lights in the hallway?",
]
