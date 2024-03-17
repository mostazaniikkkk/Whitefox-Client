import lodestone

class Player:
    def __init__(self, ip, port, username, auth):
        self.bot = lodestone.createBot(
            host = ip,
            username = username,
        )

    def chat(self, message): self.bot.chat(message)

    def getInput(self, walk, jump, camera):
        if walk[0] < .3 or walk[1] < .3 or walk[0] > -.3 or walk[1] > -.3: self.sneak = True
        else: self.sneak = False

        if walk[0] > .1: self.walkX = [True, False]
        elif walk[0] < -.1: self.walkX = [False, True]
        else: self.walkX = [False, False]

        if walk[1] > .1: self.walkY = [True, False]
        elif walk[1] < -.1: self.walkY = [False, True]
        else: self.walkY = [False, False]

        self.bot.set_control_state("sneak", self.sneak)
        self.bot.set_control_state("jump", jump)

        self.bot.set_control_state("forward", self.walkX[0])
        self.bot.set_control_state("backward", self.walkX[1])
        self.bot.set_control_state("left", self.walkY[0])
        self.bot.set_control_state("right", self.walkY[1])